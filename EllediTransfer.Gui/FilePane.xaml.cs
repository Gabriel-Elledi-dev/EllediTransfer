using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using EllediTransfer.Gui.Services;
using EllediTransfer.Gui.Terminal;
using EllediTransfer.Gui.ViewModels;

namespace EllediTransfer.Gui;

/// <summary>
/// One bank of the window: the PC (left bank only, the default) or a server, with its own
/// connection, folder, selection and opened files.
/// </summary>
public partial class FilePane : UserControl
{
    public const string DragFormat = "EllediTransfer.Pane";

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(FilePane));
    public static readonly DependencyProperty AccentSoftBrushProperty =
        DependencyProperty.Register(nameof(AccentSoftBrush), typeof(Brush), typeof(FilePane));
    public static readonly DependencyProperty PermissionsWidthProperty =
        DependencyProperty.Register(nameof(PermissionsWidth), typeof(GridLength), typeof(FilePane), new PropertyMetadata(new GridLength(0)));

    private readonly ObservableCollection<FileItem> _items = [];
    private (string Key, bool Ascending) _sort = ("Name", true);
    private SessionOptions? _pendingOptions;
    private string? _pendingFingerprint;
    private Point _dragStart;
    private IPaneHost _host = null!;

    public FilePane()
    {
        InitializeComponent();
        List.ItemsSource = _items;
    }

    /// <summary>The left bank can show the PC; the right one always shows a server.</summary>
    public bool IsLeft { get; set; }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public Brush AccentSoftBrush
    {
        get => (Brush)GetValue(AccentSoftBrushProperty);
        set => SetValue(AccentSoftBrushProperty, value);
    }

    public GridLength PermissionsWidth
    {
        get => (GridLength)GetValue(PermissionsWidthProperty);
        set => SetValue(PermissionsWidthProperty, value);
    }

    /// <summary>Null while the bank shows the PC or is not connected.</summary>
    public RemoteEndpoint? Endpoint { get; private set; }

    /// <summary>The bank shows the PC (left bank only).</summary>
    public bool IsLocal => IsLeft && ModePc.IsChecked == true;

    public bool IsConnected => Endpoint != null;

    /// <summary>Current folder: a Windows path ("" = drive list) on the PC, a POSIX path on a server.</summary>
    public string Directory { get; private set; } = "";

    /// <summary>Files can be put here: an open PC folder or a connected server.</summary>
    public bool CanReceive => IsLocal ? Directory.Length > 0 : IsConnected;

    /// <summary>Short name used in labels: "PC" or the server host.</summary>
    public string ShortName => IsLocal ? "PC" : Endpoint?.HostName ?? (IsLeft ? "server di sinistra" : "server di destra");

    public IReadOnlyList<FileItem> SelectedItems =>
        List.SelectedItems.Cast<FileItem>().Where(i => i.Kind != FileKind.Drive).ToList();

    public void Initialize(IPaneHost host)
    {
        _host = host;
        AccentBrush = (Brush)FindResource(IsLeft ? "Pc" : "Server");
        AccentSoftBrush = (Brush)FindResource(IsLeft ? "PcSoft" : "ServerSoft");
        ModeSwitch.Visibility = IsLeft ? Visibility.Visible : Visibility.Collapsed;
        if (!IsLeft)
        {
            ConnectSubtitle.Text = "I file del server compariranno qui, di fronte a quelli dell'altro lato.";
        }
        else
        {
            ConnectTitle.Text = "Collega un secondo server";
            ConnectSubtitle.Text = "Con un server su ogni lato puoi copiare file dall'uno all'altro. I file passano dal PC senza restarci.";
        }

        LoadProfile(IsLeft ? host.Settings.LeftConnection : host.Settings.LastConnection);
        if (IsLeft)
        {
            var start = host.Settings.LocalDirectory ?? host.Settings.LastConnection.LocalDirectory;
            LoadLocal(!string.IsNullOrEmpty(start) && System.IO.Directory.Exists(start)
                ? start
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        UpdateLayoutForState();
    }

    #region State and header

    private void UpdateLayoutForState()
    {
        var showServer = !IsLocal;
        var connected = IsConnected;

        BrowsePanel.Visibility = !showServer || connected ? Visibility.Visible : Visibility.Collapsed;
        ConnectPanel.Visibility = showServer && !connected ? Visibility.Visible : Visibility.Collapsed;
        // The right bank's connect form has its own big title; the left one keeps the switch visible
        Header.Visibility = IsLeft || connected ? Visibility.Visible : Visibility.Collapsed;
        Actions.Visibility = !showServer || connected ? Visibility.Visible : Visibility.Collapsed;
        TerminalButton.Visibility = connected && Endpoint!.IsSsh ? Visibility.Visible : Visibility.Collapsed;
        DisconnectButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        SessionLine.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        PermissionsWidth = showServer ? new GridLength(54) : new GridLength(0);

        SideGlyph.Text = showServer ? "" : "";
        TitleText.Text = !showServer ? "Questo PC" : connected ? Endpoint!.HostName : "Server";

        if (connected)
        {
            var endpoint = Endpoint!;
            SessionText.Text = endpoint.Label;
            SecurityChip.Background = (Brush)FindResource(endpoint.IsSecure ? "PcSoft" : "DangerSoft");
            SecurityGlyph.Foreground = SecurityText.Foreground = (Brush)FindResource(endpoint.IsSecure ? "Pc" : "Danger");
            SecurityGlyph.Text = endpoint.IsSecure ? "" : "";
            SecurityText.Text = endpoint.IsSsh ? "Chiave del server verificata" :
                endpoint.IsSecure ? "Connessione cifrata con TLS" : "Connessione non cifrata";
        }
        UpdateFooter();
        _host?.PaneChanged(this);
    }

    private async void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_host == null)
        {
            return;
        }
        if (IsLocal)
        {
            // Back to the PC: the second server is closed
            await DisconnectAsync();
            LoadLocal(Directory.Length > 0 && !Directory.StartsWith('/') ? Directory : _host.Settings.LocalDirectory ?? "");
        }
        else
        {
            _items.Clear();
            HideConnectMessages();
            UpdateLayoutForState();
            HostBox.Focus();
        }
    }

    private void Terminal_Click(object sender, RoutedEventArgs e) => OpenTerminal();

    public void OpenTerminal()
    {
        if (Endpoint is { IsSsh: true } endpoint)
        {
            new TerminalWindow(endpoint.Options).Show();
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await DisconnectAsync();
        if (IsLeft)
        {
            ModePc.IsChecked = true;
        }
        _host.SetStatus("Disconnesso.");
    }

    public async Task DisconnectAsync()
    {
        var endpoint = Endpoint;
        if (endpoint == null)
        {
            return;
        }
        Endpoint = null;
        endpoint.CloseOpenedFiles();
        endpoint.CancelTransfers();
        try
        {
            await endpoint.Browser.RunAsync(s => s.Close());
        }
        catch (Exception ex) when (ex is SessionException or IOException or InvalidOperationException)
        {
        }
        endpoint.Dispose();
        _items.Clear();
        PasswordBox.Clear();
        UpdateLayoutForState();
    }

    #endregion

    #region Connection form

    private void LoadProfile(ConnectionProfile profile)
    {
        HostBox.Text = profile.HostName;
        UserBox.Text = profile.UserName;
        KeyFileBox.Text = profile.PrivateKeyPath ?? "";
        var protocolButton = (profile.Protocol, profile.FtpSecure) switch
        {
            (Protocol.Ftp, FtpSecure.Explicit) => ProtoFtps,
            (Protocol.Ftp, FtpSecure.Implicit) => ProtoFtpsImplicit,
            (Protocol.Ftp, _) => ProtoFtp,
            _ => ProtoSftp,
        };
        protocolButton.IsChecked = true;
        PortBox.Text = (profile.Port > 0 ? profile.Port : DefaultPort()).ToString(CultureInfo.InvariantCulture);
    }

    private int DefaultPort() =>
        ProtoSftp.IsChecked == true ? 22 : ProtoFtpsImplicit.IsChecked == true ? 990 : 21;

    private void Protocol_Checked(object sender, RoutedEventArgs e)
    {
        if (KeyFileRow == null || PortBox == null)
        {
            return;
        }
        KeyFileRow.Visibility = ProtoSftp.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (PortBox.Text is "" or "21" or "22" or "990")
        {
            PortBox.Text = DefaultPort().ToString(CultureInfo.InvariantCulture);
        }
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Scegli la chiave privata",
            Filter = "Chiavi OpenSSH o PuTTY|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            KeyFileBox.Text = dialog.FileName;
        }
    }

    private void ConnectField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Connect_Click(sender, e);
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        HideConnectMessages();
        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            ShowConnectError("Scrivi l'indirizzo del server, per esempio files.azienda.it.");
            HostBox.Focus();
            return;
        }
        if (!int.TryParse(PortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            ShowConnectError("La porta deve essere un numero tra 1 e 65535.");
            PortBox.Focus();
            return;
        }

        var options = new SessionOptions
        {
            HostName = HostBox.Text.Trim(),
            PortNumber = port,
            UserName = string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim(),
            Password = PasswordBox.Password.Length > 0 ? PasswordBox.Password : null,
        };
        if (ProtoSftp.IsChecked == true)
        {
            options.Protocol = Protocol.Sftp;
            options.SshPrivateKeyPath = string.IsNullOrWhiteSpace(KeyFileBox.Text) ? null : KeyFileBox.Text.Trim();
            options.SshHostKeyFingerprint = _host.Settings.TrustedHostKeys.GetValueOrDefault(Settings.HostKeyId(options), SessionOptions.UnknownHostKey);
        }
        else
        {
            options.Protocol = Protocol.Ftp;
            options.FtpSecure = ProtoFtps.IsChecked == true ? FtpSecure.Explicit :
                ProtoFtpsImplicit.IsChecked == true ? FtpSecure.Implicit : FtpSecure.None;
        }

        await ConnectAsync(options);
    }

    public async Task ConnectAsync(SessionOptions options)
    {
        SetConnecting(true);
        var endpoint = new RemoteEndpoint(options);
        try
        {
            await endpoint.OpenAsync();
            _host.AttachEndpoint(endpoint);
            Endpoint = endpoint;
            SaveProfile(options);
            UpdateLayoutForState();
            _host.SetStatus($"Connesso a {options.HostName}.");
            await LoadRemoteAsync(endpoint.Browser.Session.CurrentDirectory);
            List.Focus();
        }
        catch (HostKeyMismatchException ex)
        {
            endpoint.Dispose();
            _pendingOptions = options;
            _pendingFingerprint = ex.ActualFingerprint;
            HostKeyText.Text = ex.ActualFingerprint;
            if (options.SshHostKeyFingerprint != SessionOptions.UnknownHostKey)
            {
                ShowConnectError("La chiave del server è diversa da quella salvata in precedenza. Può succedere dopo una reinstallazione, " +
                                 "ma anche se qualcuno si sta spacciando per il server: verifica prima di fidarti.");
            }
            HostKeyBox.Visibility = Visibility.Visible;
            HostKeyBox.UpdateLayout();
            HostKeyBox.BringIntoView();
        }
        catch (Exception ex) when (ex is SessionException or ArgumentException or IOException)
        {
            endpoint.Dispose();
            ShowConnectError(DescribeConnectError(ex));
        }
        finally
        {
            SetConnecting(false);
        }
    }

    private static string DescribeConnectError(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("530", StringComparison.Ordinal) ||
            message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
        {
            return "Utente o password non accettati dal server. Controllali e riprova.";
        }
        if (message.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("rifiutata", StringComparison.OrdinalIgnoreCase))
        {
            return "Il server ha rifiutato la connessione su questa porta. Controlla porta e protocollo.";
        }
        return "Connessione non riuscita: " + message;
    }

    private async void TrustHostKey_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingOptions == null || _pendingFingerprint == null)
        {
            return;
        }
        var options = _pendingOptions;
        options.SshHostKeyFingerprint = _pendingFingerprint;
        _host.Settings.TrustedHostKeys[Settings.HostKeyId(options)] = _pendingFingerprint;
        _host.Settings.Save();
        HideConnectMessages();
        await ConnectAsync(options);
    }

    private void RejectHostKey_Click(object sender, RoutedEventArgs e)
    {
        _pendingOptions = null;
        _pendingFingerprint = null;
        HideConnectMessages();
    }

    private void SaveProfile(SessionOptions options)
    {
        var profile = new ConnectionProfile
        {
            Protocol = options.Protocol,
            FtpSecure = options.FtpSecure,
            HostName = options.HostName,
            Port = options.PortNumber,
            UserName = options.UserName ?? "",
            PrivateKeyPath = options.SshPrivateKeyPath,
        };
        if (IsLeft)
        {
            _host.Settings.LeftConnection = profile;
        }
        else
        {
            _host.Settings.LastConnection = profile;
        }
        _host.Settings.Save();
    }

    private void SetConnecting(bool connecting)
    {
        ConnectButton.IsEnabled = !connecting;
        ConnectButtonText.Text = connecting ? "Connessione in corso…" : "Connetti";
    }

    private void ShowConnectError(string message)
    {
        ConnectError.Text = message;
        ConnectErrorBox.Visibility = Visibility.Visible;
        ConnectErrorBox.UpdateLayout();
        ConnectErrorBox.BringIntoView();
    }

    private void HideConnectMessages()
    {
        ConnectErrorBox.Visibility = Visibility.Collapsed;
        HostKeyBox.Visibility = Visibility.Collapsed;
    }

    public void FocusConnectForm() => HostBox.Focus();

    #endregion

    #region Listing

    public async Task RefreshAsync(string? select = null)
    {
        if (IsLocal)
        {
            LoadLocal(Directory);
        }
        else if (IsConnected)
        {
            await LoadRemoteAsync(Directory, select);
        }
    }

    private void LoadLocal(string directory)
    {
        var items = new List<FileItem>();
        try
        {
            if (directory.Length == 0)
            {
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    var label = string.IsNullOrEmpty(drive.VolumeLabel) ? "Disco" : drive.VolumeLabel;
                    items.Add(new FileItem
                    {
                        Name = $"{label} ({drive.Name.TrimEnd('\\')})",
                        FullPath = drive.RootDirectory.FullName,
                        Kind = FileKind.Drive,
                    });
                }
            }
            else
            {
                var info = new DirectoryInfo(directory);
                foreach (var entry in info.EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }
                    items.Add(new FileItem
                    {
                        Name = entry.Name,
                        FullPath = entry.FullName,
                        Kind = entry is DirectoryInfo ? FileKind.Folder : FileKind.File,
                        Size = entry is FileInfo file ? file.Length : 0,
                        Modified = entry.LastWriteTime,
                    });
                }
                directory = info.FullName;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _host.SetStatus($"Non riesco ad aprire {directory}: {ex.Message}", true);
            return;
        }

        Directory = directory;
        _host.Settings.LocalDirectory = directory;
        PathBox.Text = directory.Length == 0 ? "Questo PC" : directory;
        Fill(items);
        UpdateLayoutForState();
    }

    private async Task LoadRemoteAsync(string path, string? select = null)
    {
        var endpoint = Endpoint;
        if (endpoint == null)
        {
            return;
        }
        try
        {
            var (resolved, files) = await endpoint.Browser.RunAsync(s =>
            {
                var target = s.ResolvePath(path);
                return (target, s.ListDirectory(target));
            });
            if (Endpoint != endpoint)
            {
                return;
            }
            Directory = resolved;
            PathBox.Text = resolved;
            Fill(files.Select(f => new FileItem
            {
                Name = f.Name,
                FullPath = f.FullName,
                Kind = f.IsDirectory ? FileKind.Folder : FileKind.File,
                Size = f.Length,
                Modified = f.LastWriteTimeUtc > DateTime.MinValue ? f.LastWriteTimeUtc.ToLocalTime() : null,
                Permissions = f.FilePermissions?.Octal,
                Remote = f,
            }));
            if (select != null)
            {
                SelectByPath(select);
            }
            UpdateFooter();
            _host.PaneChanged(this);
        }
        catch (Exception ex) when (ex is SessionException or InvalidOperationException)
        {
            _host.SetStatus($"Non riesco ad aprire {path} su {endpoint.HostName}: {ex.Message}", true);
        }
    }

    private async Task NavigateAsync(string path, string? select = null)
    {
        if (IsLocal)
        {
            LoadLocal(path);
            if (select != null)
            {
                SelectByPath(select);
            }
        }
        else
        {
            await LoadRemoteAsync(path, select);
        }
    }

    private async void GoUp()
    {
        if (IsLocal)
        {
            if (Directory.Length > 0)
            {
                await NavigateAsync(Path.GetDirectoryName(Directory) ?? "", Directory);
            }
        }
        else if (IsConnected && Directory != "/")
        {
            await NavigateAsync(RemotePath.GetDirectoryName(Directory), Directory);
        }
    }

    private async void Open(FileItem item)
    {
        if (item.IsContainer)
        {
            await NavigateAsync(item.FullPath);
        }
        else if (IsLocal)
        {
            try
            {
                OpenedRemoteFile.OpenWithShell(item.FullPath);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _host.SetStatus($"Non riesco ad aprire {item.Name}: {ex.Message}", true);
            }
        }
        else
        {
            await OpenRemoteFileAsync(item);
        }
    }

    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = PathBox.Text.Trim();
            if (IsLocal)
            {
                LoadLocal(text is "" or "Questo PC" ? "" : Environment.ExpandEnvironmentVariables(text));
            }
            else
            {
                await LoadRemoteAsync(text);
            }
            List.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            PathBox.Text = IsLocal && Directory.Length == 0 ? "Questo PC" : Directory;
            List.Focus();
        }
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnderMouse(e) is { } item)
        {
            Open(item);
        }
    }

    private async void List_KeyDown(object sender, KeyEventArgs e)
    {
        var selected = SelectedItems;
        var canEdit = !IsLocal || Directory.Length > 0;
        switch (e.Key)
        {
            case Key.Enter when List.SelectedItems.Count == 1:
                Open((FileItem)List.SelectedItem);
                break;
            case Key.Back:
                GoUp();
                break;
            case Key.F5:
                await RefreshAsync();
                break;
            case Key.Delete when selected.Count > 0 && canEdit:
                ConfirmDelete(selected);
                break;
            case Key.F2 when selected.Count == 1 && canEdit:
                PromptRename(selected[0]);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    #endregion

    #region File operations

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (IsLocal && Directory.Length == 0)
        {
            _host.SetStatus("Apri prima un disco o una cartella.", true);
            return;
        }
        var where = IsLocal ? "sul PC" : $"su {ShortName}";
        _host.ShowPrompt($"Nuova cartella {where}", $"Verrà creata in {Directory}.", "Nuova cartella", "Crea", false, async name =>
        {
            if (IsLocal)
            {
                var path = Path.Combine(Directory, name);
                System.IO.Directory.CreateDirectory(path);
                await NavigateAsync(Directory, path);
            }
            else
            {
                var path = RemotePath.Combine(Directory, name);
                await Endpoint!.Browser.RunAsync(s => s.CreateDirectory(path));
                await NavigateAsync(Directory, path);
            }
            _host.SetStatus($"Creata la cartella {name}.");
        });
    }

    private void ConfirmDelete(IReadOnlyList<FileItem> items)
    {
        var what = items.Count == 1 ? $"«{items[0].Name}»" : $"{items.Count} elementi";
        if (IsLocal)
        {
            _host.ShowPrompt($"Eliminare {what} dal PC?", "Finiranno nel Cestino, da cui potrai recuperarli.", null, "Sposta nel Cestino", true, _ =>
            {
                foreach (var item in items)
                {
                    if (item.Kind == FileKind.Folder)
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.FullPath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                    else
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.FullPath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                }
                LoadLocal(Directory);
                _host.SetStatus($"Spostati nel Cestino: {what}.");
                return Task.CompletedTask;
            });
            return;
        }

        var endpoint = Endpoint!;
        _host.ShowPrompt($"Eliminare {what} da {endpoint.HostName}?", "Sul server non c'è un cestino: l'eliminazione è definitiva, cartelle comprese.", null, "Elimina", true, async _ =>
        {
            var failures = await endpoint.Browser.RunAsync(s => items.SelectMany(i => s.RemoveFiles(i.FullPath).Failures).ToList());
            await RefreshAsync();
            _host.SetStatus(failures.Count > 0 ? $"Eliminazione non completa: {failures[0].Message}" : $"Eliminati da {endpoint.HostName}: {what}.", failures.Count > 0);
        });
    }

    private void PromptRename(FileItem item)
    {
        // Preselect the name without its extension, like Explorer
        var dot = item.IsContainer ? -1 : item.Name.LastIndexOf('.');
        _host.ShowPrompt("Rinomina", $"Nuovo nome per «{item.Name}».", item.Name, "Rinomina", false, async name =>
        {
            if (name == item.Name)
            {
                return;
            }
            if (IsLocal)
            {
                var target = Path.Combine(Directory, name);
                if (item.Kind == FileKind.Folder)
                {
                    System.IO.Directory.Move(item.FullPath, target);
                }
                else
                {
                    File.Move(item.FullPath, target);
                }
                await NavigateAsync(Directory, target);
            }
            else
            {
                var target = RemotePath.Combine(Directory, name);
                await Endpoint!.Browser.RunAsync(s => s.MoveFile(item.FullPath, target));
                await NavigateAsync(Directory, target);
            }
            _host.SetStatus($"Rinominato in {name}.");
        }, dot > 0 ? dot : item.Name.Length);
    }

    #endregion

    #region Server files opened locally

    /// <summary>
    /// Opens a server file in its Windows application, as WinSCP does: a temporary copy is opened
    /// and sent back to the server every time it is saved.
    /// </summary>
    private async Task OpenRemoteFileAsync(FileItem item)
    {
        var endpoint = Endpoint!;
        if (endpoint.OpenedFiles.TryGetValue(item.FullPath, out var opened) && File.Exists(opened.LocalPath))
        {
            // Already open: show the same copy, so unsent edits are not overwritten
            TryOpen(opened);
            return;
        }

        _host.SetStatus($"Apertura di {item.Name}…");
        var folder = OpenedRemoteFile.CreateFolder();
        try
        {
            var failures = await endpoint.Browser.RunAsync(s =>
                s.GetFiles(item.FullPath, folder + Path.DirectorySeparatorChar, false, new TransferOptions()).Failures);
            if (failures.Count > 0)
            {
                _host.SetStatus($"Non riesco ad aprire {item.Name}: {failures[0].Message}", true);
                return;
            }
        }
        catch (Exception ex) when (ex is SessionException or IOException or InvalidOperationException)
        {
            _host.SetStatus($"Non riesco ad aprire {item.Name}: {ex.Message}", true);
            return;
        }

        opened = OpenedRemoteFile.Watch(item.FullPath, Path.Combine(folder, item.Name), Dispatcher);
        opened.Saved += file => _ = SendBackAsync(endpoint, file);
        endpoint.OpenedFiles[item.FullPath] = opened;
        if (TryOpen(opened))
        {
            _host.SetStatus($"Aperto {item.Name}. Quando salvi, le modifiche vengono inviate a {endpoint.HostName}.");
        }
    }

    private bool TryOpen(OpenedRemoteFile file)
    {
        try
        {
            file.Open();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _host.SetStatus($"Non riesco ad aprire {Path.GetFileName(file.LocalPath)}: {ex.Message}", true);
            return false;
        }
    }

    private async Task SendBackAsync(RemoteEndpoint endpoint, OpenedRemoteFile file)
    {
        if (Endpoint != endpoint || !endpoint.OpenedFiles.ContainsValue(file))
        {
            return;
        }
        if (file.Elevation is { } elevation)
        {
            // Already saved with sudo once: the normal account cannot write it, go straight there
            await SaveElevatedAsync(file, elevation.User, elevation.Password);
            return;
        }

        var name = RemotePath.GetFileName(file.RemotePath);
        var direction = IsLeft ? TransferDirection.Download : TransferDirection.Upload;
        var placeholder = new TransferItem { Name = name, Destination = file.RemotePath, Direction = direction, State = TransferState.Waiting };
        var failures = await _host.RunJobAsync(placeholder, direction, async () =>
            {
                var worker = await endpoint.GetTransferWorkerAsync();
                return await worker.RunAsync(s => s.PutFiles(file.LocalPath, file.RemotePath, false, new TransferOptions()).Failures);
            },
            f => f.Count == 0 ? $"Modifiche a {name} salvate su {endpoint.HostName}." : null);

        if (failures.Any(f => f.IsPermissionDenied) && endpoint.IsSsh)
        {
            _host.ShowSudoPrompt(this, file);
        }
    }

    /// <summary>Saves an opened file through sudo; returns null on success or the reason it failed.</summary>
    public async Task<string?> SaveElevatedAsync(OpenedRemoteFile file, string user, string password)
    {
        var endpoint = Endpoint;
        if (endpoint == null)
        {
            return "Il server non è più connesso.";
        }
        var name = RemotePath.GetFileName(file.RemotePath);
        var row = new TransferItem
        {
            Name = $"{name} (sudo)",
            Destination = file.RemotePath,
            Direction = IsLeft ? TransferDirection.Download : TransferDirection.Upload,
            State = TransferState.Running,
        };
        _host.AddTransferRow(row);
        var options = endpoint.Options;
        try
        {
            await Services.Background.RunAsync(() => ElevatedSave.Save(options, user, password, file.LocalPath, file.RemotePath));
            row.Total = new FileInfo(file.LocalPath).Length;
            row.State = TransferState.Done;
            _host.SetStatus($"Modifiche a {name} salvate su {endpoint.HostName} con sudo come {user}.");
            await RefreshAsync();
            return null;
        }
        catch (Exception ex) when (ex is SessionException or IOException)
        {
            row.Error = ex.Message;
            row.State = TransferState.Failed;
            _host.SetStatus($"{name} non salvato su {endpoint.HostName}: {ex.Message}", true);
            if (file.Elevation != null)
            {
                // The remembered account stopped working (password changed?): ask again
                file.Elevation = null;
                _host.ShowSudoPrompt(this, file);
            }
            return ex.Message;
        }
    }

    #endregion

    #region Drag and drop

    /// <summary>What a drag carries between banks: the source bank and the dragged paths.</summary>
    public sealed record DragPayload(FilePane Source, string[] Paths);

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || ItemUnderMouse(e) == null || SelectedItems is not { Count: > 0 } items)
        {
            return;
        }
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) <= SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) <= SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        if (IsLocal && Directory.Length == 0)
        {
            return;
        }

        var paths = items.Select(i => i.FullPath).ToArray();
        var data = new DataObject();
        data.SetData(DragFormat, new DragPayload(this, paths));
        if (IsLocal)
        {
            // PC files can also be dropped into Explorer
            data.SetData(DataFormats.FileDrop, paths);
        }
        DragDrop.DoDragDrop(List, data, DragDropEffects.Copy);
    }

    private bool Accepts(DragEventArgs e)
    {
        if (!CanReceive)
        {
            return false;
        }
        if (e.Data.GetData(DragFormat) is DragPayload payload)
        {
            return payload.Source != this;
        }
        // Explorer files go to a server bank
        return !IsLocal && e.Data.GetDataPresent(DataFormats.FileDrop);
    }

    private void Pane_DragOver(object sender, DragEventArgs e)
    {
        var accepted = Accepts(e);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Pane_DragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private void Pane_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (!Accepts(e))
        {
            return;
        }
        if (e.Data.GetData(DragFormat) is DragPayload payload)
        {
            _host.FilesDropped(this, payload.Source, payload.Paths);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            _host.FilesDropped(this, null, files);
        }
    }

    #endregion

    #region Sorting, selection, footer

    private void Fill(IEnumerable<FileItem> items)
    {
        _items.Clear();
        var containersFirst = items.OrderBy(i => i.IsContainer ? 0 : 1);
        var sorted = (_sort.Key, _sort.Ascending) switch
        {
            ("Size", true) => containersFirst.ThenBy(i => i.Size).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            ("Size", false) => containersFirst.ThenByDescending(i => i.Size).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            ("Modified", true) => containersFirst.ThenBy(i => i.Modified),
            ("Modified", false) => containersFirst.ThenByDescending(i => i.Modified),
            (_, false) => containersFirst.ThenByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => containersFirst.ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        foreach (var item in sorted)
        {
            _items.Add(item);
        }
        UpdateSortHeaders();
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var key = (string)((Button)sender).Tag;
        _sort = _sort.Key == key ? (key, !_sort.Ascending) : (key, key == "Name");
        Fill(_items.ToList());
    }

    private void UpdateSortHeaders()
    {
        SetHeader(SortName, "Nome");
        SetHeader(SortSize, "Dimensione");
        SetHeader(SortDate, "Modificato");
    }

    private void SetHeader(Button header, string text)
    {
        var active = (string)header.Tag == _sort.Key;
        header.Content = active ? $"{text} {(_sort.Ascending ? "↑" : "↓")}" : text;
        header.Foreground = (Brush)FindResource(active ? "Ink" : "Muted");
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFooter();
        _host?.PaneChanged(this);
    }

    private void UpdateFooter()
    {
        if (_items.Count == 0)
        {
            Footer.Text = "Cartella vuota";
            return;
        }
        var bytes = _items.Where(i => i.Kind == FileKind.File).Sum(i => i.Size);
        var text = $"{Count(_items.Count)}, {FileItem.FormatSize(bytes)}";
        Footer.Text = List.SelectedItems.Count > 0 ? $"{text}. Selezionati: {List.SelectedItems.Count}" : text;
    }

    public static string Count(int n) => n == 1 ? "1 elemento" : $"{n} elementi";

    private FileItem? ItemUnderMouse(MouseEventArgs e)
    {
        var element = List.InputHitTest(e.GetPosition(List)) as DependencyObject;
        while (element != null && element is not ListViewItem)
        {
            element = VisualTreeHelper.GetParent(element);
        }
        return (element as ListViewItem)?.DataContext as FileItem;
    }

    private void SelectByPath(string path)
    {
        var match = _items.FirstOrDefault(i => string.Equals(i.FullPath.TrimEnd('\\', '/'), path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            List.SelectedItem = match;
            List.ScrollIntoView(match);
        }
    }

    public void FocusList() => List.Focus();

    // Type-to-select, as in Explorer: letters typed within a second form a prefix to jump to;
    // repeating the same letter moves to the next name that starts with it
    private static readonly TimeSpan TypeAheadTimeout = TimeSpan.FromSeconds(1);
    private string _typeAhead = "";
    private DateTime _lastTypeAhead;

    private void List_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]) || _items.Count == 0)
        {
            return;
        }
        var now = DateTime.UtcNow;
        if (now - _lastTypeAhead > TypeAheadTimeout)
        {
            _typeAhead = "";
        }
        _lastTypeAhead = now;
        if (_typeAhead.Length == 0 && e.Text == " ")
        {
            return;
        }
        _typeAhead += e.Text;

        var match = FindTypeAheadMatch(_typeAhead, List.SelectedIndex);
        if (match != null)
        {
            List.SelectedItem = match;
            List.ScrollIntoView(match);
            List.UpdateLayout();
            // Move the keyboard focus too, so the arrow keys continue from the found item
            (List.ItemContainerGenerator.ContainerFromItem(match) as ListViewItem)?.Focus();
        }
        e.Handled = true;
    }

    private FileItem? FindTypeAheadMatch(string typed, int current)
    {
        var sameLetter = typed.All(c => char.ToLowerInvariant(c) == char.ToLowerInvariant(typed[0]));
        // Same letter pressed again: next item with that initial; a longer prefix: refine from the current item
        var prefix = sameLetter ? typed[..1] : typed;
        var start = sameLetter ? current + 1 : Math.Max(current, 0);
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[(start + i) % _items.Count];
            if (item.Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                return item;
            }
        }
        return null;
    }

    #endregion
}
