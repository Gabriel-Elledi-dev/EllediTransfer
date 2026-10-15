using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EllediTransfer.Gui.Services;
using EllediTransfer.Gui.ViewModels;

namespace EllediTransfer.Gui;

public partial class MainWindow : Window, IPaneHost
{
    private const int MaxQueueRows = 200;

    private readonly ObservableCollection<TransferItem> _transfers = [];
    private readonly Dictionary<string, TransferItem> _activeTransfers = [];
    // One job at a time across both banks: a predictable queue, and "Annulla" stops everything
    private readonly SemaphoreSlim _jobGate = new(1, 1);
    private int _runningJobs;
    private int _queueGeneration;

    // Set while a job runs, so progress rows get the job's direction and relay stage
    private TransferDirection _jobDirection;
    private RelayContext? _relay;

    private Func<string, Task>? _promptAction;

    public MainWindow()
    {
        InitializeComponent();
        TransferList.ItemsSource = _transfers;
        _transfers.CollectionChanged += (_, _) => UpdateQueueUi();

        LeftPane.Initialize(this);
        RightPane.Initialize(this);
        PaneChanged(RightPane);

        Loaded += (_, _) => RightPane.FocusConnectForm();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Terminal on the bank that has the keyboard focus, otherwise on the right server
                (LeftPane.IsKeyboardFocusWithin && LeftPane.IsConnected ? LeftPane : RightPane).OpenTerminal();
                e.Handled = true;
            }
        };
        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        };
        OpenedRemoteFile.CleanOldFolders();
        Closing += (_, _) =>
        {
            Settings.Save();
            LeftPane.Endpoint?.Dispose();
            RightPane.Endpoint?.Dispose();
        };
    }

    public Settings Settings { get; } = Settings.Load();

    #region Banks and direction buttons

    public void PaneChanged(FilePane pane)
    {
        if (UploadButton == null)
        {
            return;
        }
        UpdateDirection(UploadButton, UploadTitle, UploadHint, LeftPane, RightPane);
        UpdateDirection(DownloadButton, DownloadTitle, DownloadHint, RightPane, LeftPane);

        // Synchronization compares a PC folder with a server folder
        SyncButton.IsEnabled = LeftPane.IsLocal && LeftPane.Directory.Length > 0 && RightPane.IsConnected;
        SyncButton.ToolTip = LeftPane.IsLocal ? null : "La sincronizzazione confronta una cartella del PC con una del server.";
    }

    private static void UpdateDirection(System.Windows.Controls.Button button, System.Windows.Controls.TextBlock title,
        System.Windows.Controls.TextBlock hint, FilePane from, FilePane to)
    {
        title.Text = to.IsLocal ? "Scarica sul PC" :
            from.IsLocal ? "Invia al server" :
            to.IsConnected ? $"Copia su {to.ShortName}" : $"Copia sul {to.ShortName}";
        var selected = from.IsLocal || from.IsConnected ? from.SelectedItems.Count : 0;
        button.IsEnabled = selected > 0 && to.CanReceive;
        hint.Text = !from.IsLocal && !from.IsConnected ? $"Collega il {from.ShortName}" :
            !to.CanReceive ? (to.IsLocal ? "Apri una cartella sul PC" : $"Collega il {to.ShortName}") :
            selected == 0 ? (from.IsLocal ? "Seleziona file sul PC" : $"Seleziona file su {from.ShortName}") :
            $"{FilePane.Count(selected)} da {from.ShortName}";
    }

    private void Upload_Click(object sender, RoutedEventArgs e) => StartTransfer(LeftPane, RightPane, LeftPane.SelectedItems.Select(i => i.FullPath).ToList());

    private void Download_Click(object sender, RoutedEventArgs e) => StartTransfer(RightPane, LeftPane, RightPane.SelectedItems.Select(i => i.FullPath).ToList());

    public void FilesDropped(FilePane target, FilePane? source, IReadOnlyList<string> paths)
    {
        // Files from Explorer come from the PC
        var from = source ?? (target == LeftPane ? RightPane : LeftPane);
        if (source == null)
        {
            _ = TransferFromExplorerAsync(target, paths);
            return;
        }
        StartTransfer(from, target, paths);
    }

    private async void StartTransfer(FilePane from, FilePane to, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }
        if (!to.CanReceive)
        {
            SetStatus(to.IsLocal ? "Apri sul PC la cartella in cui copiare i file." : "Collega prima il server di destinazione.", true);
            return;
        }
        await TransferAsync(from, to, paths);
    }

    #endregion

    #region Transfers

    private TransferOptions BuildTransferOptions()
    {
        var options = new TransferOptions
        {
            TransferMode = ModeText.IsChecked == true ? TransferMode.Ascii :
                ModeAuto.IsChecked == true ? TransferMode.Automatic : TransferMode.Binary,
            PreserveTimestamp = PreserveTimeCheck.IsChecked == true,
        };
        if (int.TryParse(SpeedBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var speed))
        {
            options.SpeedLimit = speed;
        }
        return options;
    }

    private Task TransferFromExplorerAsync(FilePane target, IReadOnlyList<string> paths)
    {
        var endpoint = target.Endpoint!;
        return RunCopyJobAsync(paths, target == LeftPane ? TransferDirection.Download : TransferDirection.Upload,
            $"su {endpoint.HostName}", async (options, remove) =>
            {
                var worker = await endpoint.GetTransferWorkerAsync();
                return await worker.RunAsync(s => paths.SelectMany(p => s.PutFiles(p, RemotePath.Combine(target.Directory, ""), remove, options).Failures).ToList());
            });
    }

    /// <summary>Copies between the banks: PC to server, server to PC, or server to server through the PC.</summary>
    private Task TransferAsync(FilePane from, FilePane to, IReadOnlyList<string> paths)
    {
        var direction = from == LeftPane ? TransferDirection.Upload : TransferDirection.Download;
        var targetDirectory = to.Directory;
        var where = to.IsLocal ? "sul PC" : $"su {to.ShortName}";

        if (from.IsLocal)
        {
            var endpoint = to.Endpoint!;
            return RunCopyJobAsync(paths, direction, where, async (options, remove) =>
            {
                var worker = await endpoint.GetTransferWorkerAsync();
                return await worker.RunAsync(s => paths.SelectMany(p => s.PutFiles(p, RemotePath.Combine(targetDirectory, ""), remove, options).Failures).ToList());
            });
        }
        if (to.IsLocal)
        {
            var endpoint = from.Endpoint!;
            return RunCopyJobAsync(paths, direction, where, async (options, remove) =>
            {
                var worker = await endpoint.GetTransferWorkerAsync();
                var local = Path.TrimEndingDirectorySeparator(targetDirectory) + Path.DirectorySeparatorChar;
                return await worker.RunAsync(s => paths.SelectMany(p => s.GetFiles(p, local, remove, options).Failures).ToList());
            });
        }
        return RunCopyJobAsync(paths, direction, where, (options, remove) => RelayAsync(from.Endpoint!, to.Endpoint!, paths, targetDirectory, options, remove));
    }

    private sealed record RelayContext(string SourceRoot, string TempRoot, string Fetch, string Send);

    /// <summary>
    /// Server to server: no protocol copies directly between two servers, so each item is fetched into a
    /// private temporary folder on the PC and sent on; the folder is deleted afterwards.
    /// </summary>
    private async Task<List<SessionRemoteException>> RelayAsync(RemoteEndpoint source, RemoteEndpoint target,
        IReadOnlyList<string> paths, string targetDirectory, TransferOptions options, bool remove)
    {
        var temp = Path.Combine(Path.GetTempPath(), "EllediTransfer", "Copie", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(temp);
        var sourceRoot = RemotePath.GetDirectoryName(paths[0]);
        await Dispatcher.InvokeAsync(() => _relay = new RelayContext(sourceRoot, temp, $"Prelievo da {source.HostName}", $"Invio a {target.HostName}"));
        try
        {
            var from = await source.GetTransferWorkerAsync();
            var to = await target.GetTransferWorkerAsync();
            var failures = await from.RunAsync(s =>
                paths.SelectMany(p => s.GetFiles(p, temp + Path.DirectorySeparatorChar, false, options).Failures).ToList());
            if (failures.Count > 0)
            {
                return failures;
            }

            var fetched = Directory.EnumerateFileSystemEntries(temp).ToList();
            failures = await to.RunAsync(s =>
                fetched.SelectMany(p => s.PutFiles(p, RemotePath.Combine(targetDirectory, ""), false, options).Failures).ToList());

            // "Move" removes the originals only once the copy arrived
            if (failures.Count == 0 && remove)
            {
                failures = await from.RunAsync(s => paths.SelectMany(p => s.RemoveFiles(p).Failures).ToList());
            }
            return failures;
        }
        finally
        {
            await Dispatcher.InvokeAsync(() => _relay = null);
            try
            {
                Directory.Delete(temp, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task RunCopyJobAsync(IReadOnlyList<string> paths, TransferDirection direction, string where,
        Func<TransferOptions, bool, Task<List<SessionRemoteException>>> copy)
    {
        var options = BuildTransferOptions();
        var remove = MoveCheck.IsChecked == true;
        var name = paths.Count == 1 ? DisplayName(paths[0]) : $"{paths.Count} elementi";
        var placeholder = new TransferItem { Name = name, Destination = where, Direction = direction, State = TransferState.Waiting };
        await RunJobAsync(placeholder, direction, () => copy(options, remove),
            failures => failures.Count == 0 ? $"{(remove ? "Spostato" : "Copiato")} {where}: {name}." : null);
    }

    /// <summary>
    /// Runs one job on the shared queue: a waiting row is shown until it starts, file rows appear
    /// through the transfer events, both banks are refreshed at the end.
    /// </summary>
    public async Task<List<SessionRemoteException>> RunJobAsync(TransferItem placeholder, TransferDirection direction,
        Func<Task<List<SessionRemoteException>>> job, Func<List<SessionRemoteException>, string?> successMessage)
    {
        AddTransferRow(placeholder);
        _runningJobs++;
        UpdateQueueUi();
        var generation = _queueGeneration;
        var cancelled = false;
        var failures = new List<SessionRemoteException>();
        await _jobGate.WaitAsync();
        try
        {
            // Jobs still waiting when the user pressed "Annulla" are dropped
            if (generation != _queueGeneration)
            {
                throw new OperationCanceledException();
            }
            _transfers.Remove(placeholder);
            _jobDirection = direction;
            failures = await job();
            var message = successMessage(failures);
            if (message != null)
            {
                SetStatus(message);
            }
            else if (failures.Count > 0)
            {
                SetStatus(failures.Count == 1 ? $"Un errore: {failures[0].Message}" : $"{failures.Count} errori, il primo: {failures[0].Message}", true);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex) when (ex is SessionException or IOException or InvalidOperationException or ArgumentException)
        {
            // A failure after cancelling is the aborted stream closing, not a real error
            cancelled = generation != _queueGeneration;
            if (!cancelled)
            {
                SetStatus("Trasferimento non riuscito: " + ex.Message, true);
                failures.Add(ex as SessionRemoteException ?? new SessionRemoteException(ex.Message, ex));
            }
        }
        finally
        {
            _jobGate.Release();
            _transfers.Remove(placeholder);
            _runningJobs--;
            if (cancelled)
            {
                MarkActiveCancelled();
                SetStatus("Trasferimento annullato. I file interrotti riprenderanno da dove si erano fermati.");
            }
            if (_runningJobs == 0)
            {
                LeftPane.Endpoint?.ReleaseRetired();
                RightPane.Endpoint?.ReleaseRetired();
            }
            UpdateQueueUi();
            await LeftPane.RefreshAsync();
            await RightPane.RefreshAsync();
        }
        return failures;
    }

    /// <summary>Called by the banks when a server connects, so its transfer events reach the queue.</summary>
    public void AttachEndpoint(RemoteEndpoint endpoint)
    {
        endpoint.TransferProgress += (_, e) => Dispatcher.InvokeAsync(() => OnTransferProgress(e));
        endpoint.FileTransferred += (_, e) => Dispatcher.InvokeAsync(() => OnFileTransferred(e));
    }

    /// <summary>
    /// Rows are keyed by file; in a server-to-server copy the fetch and the send of the same file
    /// share one row (key relative to the source folder or to the temporary folder).
    /// </summary>
    private string RowKey(string fileName)
    {
        if (_relay is { } relay)
        {
            if (fileName.StartsWith(relay.TempRoot, StringComparison.OrdinalIgnoreCase))
            {
                return "relay:" + fileName[relay.TempRoot.Length..].Replace('\\', '/').TrimStart('/');
            }
            if (fileName.StartsWith(relay.SourceRoot, StringComparison.Ordinal))
            {
                return "relay:" + fileName[relay.SourceRoot.Length..].TrimStart('/');
            }
        }
        return fileName;
    }

    private string? RowStage(string fileName) =>
        _relay is { } relay ? fileName.StartsWith(relay.TempRoot, StringComparison.OrdinalIgnoreCase) ? relay.Send : relay.Fetch : null;

    private void OnTransferProgress(FileTransferProgressEventArgs e)
    {
        var key = RowKey(e.FileName);
        if (!_activeTransfers.TryGetValue(key, out var item))
        {
            item = new TransferItem { Name = DisplayName(e.FileName), Destination = "", Direction = _jobDirection, State = TransferState.Running };
            _activeTransfers[key] = item;
            AddTransferRow(item);
        }
        item.Stage = RowStage(e.FileName);
        item.State = TransferState.Running;
        item.Total = e.Total;
        item.Transferred = e.Transferred;
        item.Speed = e.CPS;
    }

    private void OnFileTransferred(TransferEventArgs e)
    {
        var key = RowKey(e.FileName);
        var fetchedForRelay = _relay != null && !e.FileName.StartsWith(_relay.TempRoot, StringComparison.OrdinalIgnoreCase);
        if (!_activeTransfers.TryGetValue(key, out var item))
        {
            item = new TransferItem { Name = DisplayName(e.FileName), Destination = e.Destination, Direction = _jobDirection };
            _activeTransfers[key] = item;
            AddTransferRow(item);
        }
        if (e.Error != null)
        {
            item.Error = e.Error.Message;
            item.State = TransferState.Failed;
            _activeTransfers.Remove(key);
        }
        else if (fetchedForRelay)
        {
            // Half way: fetched, the send follows on the same row
            item.Stage = RowStage(e.FileName);
            item.Transferred = 0;
        }
        else
        {
            item.Stage = null;
            item.Total = e.Length;
            item.State = TransferState.Done;
            _activeTransfers.Remove(key);
        }
    }

    private void MarkActiveCancelled()
    {
        foreach (var item in _activeTransfers.Values)
        {
            item.State = TransferState.Cancelled;
        }
        _activeTransfers.Clear();
        foreach (var waiting in _transfers.Where(t => t.State == TransferState.Waiting).ToList())
        {
            _transfers.Remove(waiting);
        }
    }

    /// <summary>Stops the running transfer and drops the queued ones on both banks.</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _queueGeneration);
        LeftPane.Endpoint?.CancelTransfers();
        RightPane.Endpoint?.CancelTransfers();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _transfers.Where(t => t.State is TransferState.Done or TransferState.Failed or TransferState.Cancelled).ToList())
        {
            _transfers.Remove(item);
        }
    }

    public void AddTransferRow(TransferItem item)
    {
        _transfers.Insert(0, item);
        while (_transfers.Count > MaxQueueRows)
        {
            _transfers.RemoveAt(_transfers.Count - 1);
        }
    }

    private void UpdateQueueUi()
    {
        QueueEmpty.Visibility = _transfers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = _runningJobs > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = _runningJobs == 0 && _transfers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string DisplayName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    #endregion

    #region Synchronization

    private void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (!LeftPane.IsLocal || LeftPane.Directory.Length == 0 || !RightPane.IsConnected)
        {
            SetStatus("Per sincronizzare apri a sinistra una cartella del PC e a destra un server.", true);
            return;
        }
        SyncLocalPath.Text = LeftPane.Directory;
        SyncRemotePath.Text = RightPane.Directory;
        ResetSyncResults();
        ShowPanel(SyncPanel);
        SyncCompareButton.Focus();
    }

    private (SynchronizationMode Mode, bool Delete, SynchronizationCriteria Criteria) SyncSettings()
    {
        var mode = SyncToPc.IsChecked == true ? SynchronizationMode.Local :
            SyncBoth.IsChecked == true ? SynchronizationMode.Both : SynchronizationMode.Remote;
        var criteria = mode == SynchronizationMode.Both || CriteriaTime.IsChecked == true ? SynchronizationCriteria.Time :
            CriteriaSize.IsChecked == true ? SynchronizationCriteria.Size : SynchronizationCriteria.Either;
        return (mode, mode != SynchronizationMode.Both && SyncDeleteCheck.IsChecked == true, criteria);
    }

    private void SyncOption_Changed(object sender, RoutedEventArgs e)
    {
        if (SyncDeleteCheck == null || CriteriaSize == null)
        {
            return;
        }
        var both = SyncBoth.IsChecked == true;
        SyncDeleteCheck.IsEnabled = !both;
        CriteriaSize.IsEnabled = CriteriaEither.IsEnabled = !both;
        if (both)
        {
            CriteriaTime.IsChecked = true;
        }
        ResetSyncResults();
    }

    private void ResetSyncResults()
    {
        if (SyncResultBox == null)
        {
            return;
        }
        SyncResultBox.Visibility = Visibility.Collapsed;
        SyncRunButton.IsEnabled = false;
        SyncRunButton.Content = "Sincronizza";
    }

    private async void SyncCompare_Click(object sender, RoutedEventArgs e)
    {
        var (mode, delete, criteria) = SyncSettings();
        var local = LeftPane.Directory;
        var remote = RightPane.Directory;
        var endpoint = RightPane.Endpoint!;
        SyncCompareButton.IsEnabled = false;
        SyncCompareButton.Content = "Confronto in corso…";
        try
        {
            var differences = await endpoint.Browser.RunAsync(s => s.CompareDirectories(mode, local, remote, delete, false, criteria));
            SyncDifferences.ItemsSource = differences.Select(ToRow).ToList();
            SyncSummary.Text = differences.Count switch
            {
                0 => "Le due cartelle sono già allineate.",
                1 => "1 elemento da allineare",
                var n => $"{n} elementi da allineare",
            };
            SyncResultBox.Visibility = Visibility.Visible;
            SyncRunButton.IsEnabled = differences.Count > 0;
            SyncRunButton.Content = differences.Count > 0 ? $"Sincronizza {differences.Count}" : "Sincronizza";
        }
        catch (Exception ex) when (ex is SessionException or IOException or ArgumentException)
        {
            SyncSummary.Text = "Confronto non riuscito: " + ex.Message;
            SyncDifferences.ItemsSource = null;
            SyncResultBox.Visibility = Visibility.Visible;
        }
        finally
        {
            SyncCompareButton.IsEnabled = true;
            SyncCompareButton.Content = "Confronta";
        }
    }

    private DifferenceRow ToRow(ComparisonDifference d)
    {
        var (glyph, action, brush) = d.Action switch
        {
            SynchronizationAction.UploadNew => ("", "Nuovo, da inviare", "Pc"),
            SynchronizationAction.UploadUpdate => ("", "Modificato, da inviare", "Pc"),
            SynchronizationAction.DownloadNew => ("", "Nuovo, da scaricare", "Server"),
            SynchronizationAction.DownloadUpdate => ("", "Modificato, da scaricare", "Server"),
            SynchronizationAction.DeleteRemote => ("", "Da eliminare sul server", "Danger"),
            _ => ("", "Da eliminare sul PC", "Danger"),
        };
        return new DifferenceRow(glyph, action, d.RelativePath + (d.IsDirectory ? "/" : ""), (Brush)FindResource(brush));
    }

    private async void SyncRun_Click(object sender, RoutedEventArgs e)
    {
        var (mode, delete, criteria) = SyncSettings();
        var local = LeftPane.Directory;
        var remote = RightPane.Directory;
        var endpoint = RightPane.Endpoint!;
        var options = BuildTransferOptions();
        CloseOverlay();

        var direction = mode == SynchronizationMode.Local ? TransferDirection.Download : TransferDirection.Upload;
        var placeholder = new TransferItem { Name = "Sincronizzazione", Destination = remote, Direction = direction, State = TransferState.Waiting };
        await RunJobAsync(placeholder, direction, async () =>
            {
                var worker = await endpoint.GetTransferWorkerAsync();
                return await worker.RunAsync(s => s.SynchronizeDirectories(mode, local, remote, delete, false, criteria, options).Failures);
            },
            failures => failures.Count == 0 ? "Sincronizzazione completata." : null);
    }

    private void SyncClose_Click(object sender, RoutedEventArgs e) => CloseOverlay();

    #endregion

    #region Sudo

    private (FilePane Pane, OpenedRemoteFile File)? _sudoTarget;

    public void ShowSudoPrompt(FilePane pane, OpenedRemoteFile file)
    {
        _sudoTarget = (pane, file);
        SudoMessage.Text = $"Il tuo utente non ha il permesso di scrivere {file.RemotePath} su {pane.ShortName}. Per salvarlo comunque, " +
                           "usa un account che può eseguire sudo. Il file manterrà proprietario e permessi attuali.";
        if (SudoUser.Text.Length == 0)
        {
            SudoUser.Text = pane.Endpoint?.Options.UserName ?? "";
        }
        SudoPassword.Clear();
        SudoErrorBox.Visibility = Visibility.Collapsed;
        SudoConfirm.IsEnabled = true;
        SudoConfirm.Content = "Salva con sudo";
        ShowPanel(SudoPanel);
        if (SudoUser.Text.Length > 0)
        {
            SudoPassword.Focus();
        }
        else
        {
            SudoUser.Focus();
        }
    }

    private async void SudoConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_sudoTarget is not var (pane, file))
        {
            return;
        }
        var user = SudoUser.Text.Trim();
        var password = SudoPassword.Password;
        if (user.Length == 0 || password.Length == 0)
        {
            SudoError.Text = "Inserisci utente e password.";
            SudoErrorBox.Visibility = Visibility.Visible;
            return;
        }

        SudoConfirm.IsEnabled = false;
        SudoConfirm.Content = "Salvataggio…";
        var error = await pane.SaveElevatedAsync(file, user, password);
        if (error == null)
        {
            file.Elevation = (user, password);
            SudoPassword.Clear();
            _sudoTarget = null;
            CloseOverlay();
        }
        else
        {
            SudoError.Text = error;
            SudoErrorBox.Visibility = Visibility.Visible;
            SudoConfirm.IsEnabled = true;
            SudoConfirm.Content = "Salva con sudo";
            SudoPassword.Focus();
            SudoPassword.SelectAll();
        }
    }

    private void SudoPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SudoConfirm_Click(sender, e);
        }
    }

    private void SudoCancel_Click(object sender, RoutedEventArgs e)
    {
        var name = _sudoTarget is var (_, file) ? RemotePath.GetFileName(file.RemotePath) : "";
        _sudoTarget = null;
        SudoPassword.Clear();
        CloseOverlay();
        SetStatus($"Modifiche a {name} non salvate sul server. Restano nella copia aperta sul PC.", true);
    }

    #endregion

    #region Prompt and overlay

    public void ShowPrompt(string title, string message, string? input, string confirmText, bool destructive,
        Func<string, Task> onConfirm, int selectLength = -1)
    {
        PromptTitle.Text = title;
        PromptMessage.Text = message;
        PromptInput.Visibility = input == null ? Visibility.Collapsed : Visibility.Visible;
        PromptInput.Text = input ?? "";
        PromptConfirm.Content = confirmText;
        PromptConfirm.Background = (Brush)FindResource(destructive ? "Danger" : "Ink");
        _promptAction = onConfirm;
        ShowPanel(PromptPanel);
        if (input != null)
        {
            PromptInput.Focus();
            PromptInput.Select(0, selectLength >= 0 ? selectLength : input.Length);
        }
        else
        {
            PromptConfirm.Focus();
        }
    }

    private async void PromptConfirm_Click(object sender, RoutedEventArgs e)
    {
        var text = PromptInput.Text.Trim();
        if (PromptInput.Visibility == Visibility.Visible && (text.Length == 0 || text.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0))
        {
            PromptMessage.Text = "Il nome non può essere vuoto né contenere / \\ : * ? \" < > |";
            PromptInput.Focus();
            return;
        }

        var action = _promptAction;
        CloseOverlay();
        if (action == null)
        {
            return;
        }
        try
        {
            await action(text);
        }
        catch (Exception ex) when (ex is SessionException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            SetStatus(ex.Message, true);
        }
    }

    private void PromptInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            PromptConfirm_Click(sender, e);
        }
    }

    private void PromptCancel_Click(object sender, RoutedEventArgs e) => CloseOverlay();

    private void ShowPanel(FrameworkElement panel)
    {
        PromptPanel.Visibility = panel == PromptPanel ? Visibility.Visible : Visibility.Collapsed;
        SyncPanel.Visibility = panel == SyncPanel ? Visibility.Visible : Visibility.Collapsed;
        SudoPanel.Visibility = panel == SudoPanel ? Visibility.Visible : Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
    }

    private void CloseOverlay()
    {
        Overlay.Visibility = Visibility.Collapsed;
        _promptAction = null;
    }

    #endregion

    #region Window chrome and status

    public void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(error ? "Danger" : "Muted");
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    #endregion
}
