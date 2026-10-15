using System.Diagnostics;
using EllediTransfer.FileSystems;

namespace EllediTransfer;

/// <summary>
/// File transfer session modelled on the WinSCP .NET assembly API
/// (PutFiles/GetFiles/RemoveFiles/SynchronizeDirectories and friends).
/// </summary>
public sealed class Session : IDisposable
{
    private const string PartialExtension = ".filepart";
    private const int BufferSize = 32 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    // Tolerates servers that round timestamps (e.g. FTP without MLSD)
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    private IRemoteFileSystem? _fileSystem;
    private string _currentDirectory = "/";
    private volatile bool _cancelRequested;

    public event EventHandler<TransferEventArgs>? FileTransferred;
    public event EventHandler<RemovalEventArgs>? FileRemoved;
    public event EventHandler<FileTransferProgressEventArgs>? FileTransferProgress;
    public event EventHandler<string>? OutputDataReceived;

    public bool Opened => _fileSystem != null;
    public SessionOptions? Options { get; private set; }
    public string? ServerFingerprint => _fileSystem?.ServerFingerprint;
    public string CurrentDirectory => _currentDirectory;

    private IRemoteFileSystem FileSystem => _fileSystem ?? throw new InvalidOperationException("Session is not opened");

    public void Open(SessionOptions options)
    {
        if (_fileSystem != null)
        {
            throw new InvalidOperationException("Session is already opened");
        }
        options.Validate();

        IRemoteFileSystem fileSystem = options.Protocol == Protocol.Sftp
            ? new SftpFileSystem(options)
            : new FtpFileSystem(options);
        Log($"Connecting to {options} ...");
        try
        {
            fileSystem.Open();
            _currentDirectory = fileSystem.GetCurrentDirectory();
        }
        catch
        {
            fileSystem.Dispose();
            throw;
        }

        _fileSystem = fileSystem;
        Options = options;
        Log($"Session started, remote directory {_currentDirectory}");
    }

    public void Close()
    {
        if (_fileSystem == null)
        {
            return;
        }
        try
        {
            _fileSystem.Close();
        }
        finally
        {
            _fileSystem.Dispose();
            _fileSystem = null;
            Options = null;
        }
    }

    public void Dispose() => Close();

    /// <summary>
    /// Stops the running transfer operation (may be called from another thread). The operation throws
    /// <see cref="OperationCanceledException"/>; an interrupted ".filepart" file can be resumed later.
    /// </summary>
    public void CancelTransfers() => _cancelRequested = true;

    private void ThrowIfCancelled()
    {
        if (_cancelRequested)
        {
            throw new OperationCanceledException("Transfer cancelled.");
        }
    }

    public string ResolvePath(string path) =>
        RemotePath.Normalize(path.StartsWith('/') ? path : RemotePath.Combine(_currentDirectory, path));

    public void ChangeDirectory(string path)
    {
        var resolved = ResolvePath(path);
        FileSystem.ChangeDirectory(resolved);
        _currentDirectory = resolved;
    }

    public IReadOnlyList<RemoteFileInfo> ListDirectory(string path) => FileSystem.ListDirectory(ResolvePath(path));

    public RemoteFileInfo GetFileInfo(string path) =>
        FileSystem.Stat(ResolvePath(path)) ?? throw new SessionRemoteException($"{path} not found.");

    public bool FileExists(string path) => FileSystem.Stat(ResolvePath(path)) != null;

    public void CreateDirectory(string path) => FileSystem.CreateDirectory(ResolvePath(path));

    public void MoveFile(string sourcePath, string targetPath)
    {
        var source = ResolvePath(sourcePath);
        var target = ResolvePath(targetPath);
        if (targetPath.EndsWith('/') || FileSystem.Stat(target) is { IsDirectory: true })
        {
            target = RemotePath.Combine(target, RemotePath.GetFileName(source));
        }
        FileSystem.Rename(source, target);
    }

    public RemovalOperationResult RemoveFiles(string path)
    {
        var result = new RemovalOperationResult();
        foreach (var file in ResolveRemoteSources(ResolvePath(path), result))
        {
            RemoveRemote(file, result.Removals, result);
        }
        return result;
    }

    #region Upload

    public TransferOperationResult PutFiles(string localPath, string remotePath, bool remove = false, TransferOptions? options = null)
    {
        _cancelRequested = false;
        options ??= new TransferOptions();
        var result = new TransferOperationResult();
        var mask = FileMask.Parse(options.FileMask);

        var sources = ResolveLocalSources(localPath, result);
        if (sources.Count == 0)
        {
            return result;
        }

        string targetDirectory;
        string operationMask;
        try
        {
            (targetDirectory, operationMask) = ResolveRemoteTarget(remotePath);
        }
        catch (SessionRemoteException ex)
        {
            result.Failures.Add(ex);
            return result;
        }

        foreach (var source in sources)
        {
            var name = Path.GetFileName(source);
            UploadEntry(source, targetDirectory, FileMask.ApplyOperationMask(name, operationMask), name,
                mask, options, remove, result.Transfers, result);
        }
        return result;
    }

    public TransferEventArgs PutFileToDirectory(string localFilePath, string remoteDirectory, bool remove = false, TransferOptions? options = null)
    {
        if (!File.Exists(localFilePath))
        {
            throw new FileNotFoundException($"File {localFilePath} does not exist", localFilePath);
        }
        var result = PutFiles(localFilePath, RemotePath.Combine(remoteDirectory, ""), remove, options);
        result.Check();
        return result.Transfers.Single();
    }

    private void UploadEntry(string localPath, string remoteDirectory, string targetName, string relativePath,
        FileMask mask, TransferOptions options, bool remove, List<TransferEventArgs> transfers, OperationResultBase result)
    {
        var isDirectory = Directory.Exists(localPath);
        if (!mask.Matches(Path.GetFileName(localPath), isDirectory, relativePath))
        {
            return;
        }

        var target = RemotePath.Combine(remoteDirectory, targetName);
        if (!isDirectory)
        {
            transfers.Add(UploadFile(localPath, target, options, remove, result));
            return;
        }

        try
        {
            var existing = FileSystem.Stat(target);
            if (existing == null)
            {
                FileSystem.CreateDirectory(target);
            }
            else if (!existing.IsDirectory)
            {
                throw new SessionRemoteException($"Cannot create remote folder \"{target}\", a file with the same name exists.");
            }
        }
        catch (SessionRemoteException ex)
        {
            result.Failures.Add(ex);
            return;
        }

        foreach (var child in Directory.EnumerateFileSystemEntries(localPath).Order(StringComparer.OrdinalIgnoreCase))
        {
            var childName = Path.GetFileName(child);
            UploadEntry(child, target, childName, relativePath + "/" + childName, mask, options, remove, transfers, result);
        }

        if (remove && !Directory.EnumerateFileSystemEntries(localPath).Any())
        {
            TryRemoveLocal(localPath, true, result);
        }
    }

    private TransferEventArgs UploadFile(string localPath, string remotePath, TransferOptions options, bool remove, OperationResultBase result)
    {
        ThrowIfCancelled();
        var args = new TransferEventArgs { FileName = localPath, Destination = remotePath, Side = ProgressSide.Local };
        try
        {
            var local = new FileInfo(localPath);
            var ascii = options.IsAscii(local.Name);
            var existing = FileSystem.Stat(remotePath);
            if (existing?.IsDirectory == true)
            {
                throw new SessionRemoteException($"Cannot overwrite remote folder \"{remotePath}\" with a file.");
            }

            var partialPath = remotePath + PartialExtension;
            var plan = PlanTransfer(local.Length, existing?.Length, options, ascii, () => FileSystem.Stat(partialPath)?.Length);
            using (var source = OpenLocalRead(localPath, plan.SourceOffset))
            using (var target = FileSystem.OpenWrite(plan.UsePartial ? partialPath : remotePath, plan.TargetOffset))
            {
                args.Length = Copy(source, target, local.Length, plan.SourceOffset, ascii, false, options.SpeedLimit, localPath, ProgressSide.Local);
            }
            args.Resumed = plan.SourceOffset > 0;

            if (plan.UsePartial)
            {
                if (existing != null)
                {
                    FileSystem.DeleteFile(remotePath);
                }
                FileSystem.Rename(partialPath, remotePath);
            }

            if (options.PreserveTimestamp)
            {
                TrySetAttribute(args, "timestamp", () => FileSystem.SetLastWriteTime(remotePath, local.LastWriteTimeUtc));
            }
            if (options.FilePermissions != null)
            {
                TrySetAttribute(args, "permissions", () => FileSystem.SetPermissions(remotePath, options.FilePermissions));
            }
            if (remove)
            {
                args.RemovalError = TryRemoveLocal(localPath, false, result);
            }
        }
        catch (Exception ex) when (ex is SessionRemoteException or IOException or UnauthorizedAccessException)
        {
            args.Error = ToRemoteException(ex);
            result.Failures.Add(args.Error);
        }

        OnFileTransferred(args);
        return args;
    }

    #endregion

    #region Download

    public TransferOperationResult GetFiles(string remotePath, string localPath, bool remove = false, TransferOptions? options = null)
    {
        _cancelRequested = false;
        options ??= new TransferOptions();
        var result = new TransferOperationResult();
        var mask = FileMask.Parse(options.FileMask);

        List<RemoteFileInfo> sources;
        try
        {
            sources = ResolveRemoteSources(ResolvePath(remotePath), result);
        }
        catch (SessionRemoteException ex)
        {
            result.Failures.Add(ex);
            return result;
        }

        var (targetDirectory, operationMask) = ResolveLocalTarget(localPath);
        foreach (var source in sources)
        {
            DownloadEntry(source, targetDirectory, FileMask.ApplyOperationMask(source.Name, operationMask), source.Name,
                mask, options, remove, result.Transfers, result);
        }
        return result;
    }

    public TransferEventArgs GetFileToDirectory(string remoteFilePath, string localDirectory, bool remove = false, TransferOptions? options = null)
    {
        var info = GetFileInfo(remoteFilePath);
        if (info.IsDirectory)
        {
            throw new SessionRemoteException($"{remoteFilePath} is a folder, not a file.");
        }
        var result = GetFiles(remoteFilePath, Path.TrimEndingDirectorySeparator(localDirectory) + Path.DirectorySeparatorChar, remove, options);
        result.Check();
        return result.Transfers.Single();
    }

    private void DownloadEntry(RemoteFileInfo source, string localDirectory, string targetName, string relativePath,
        FileMask mask, TransferOptions options, bool remove, List<TransferEventArgs> transfers, OperationResultBase result)
    {
        if (!mask.Matches(source.Name, source.IsDirectory, relativePath))
        {
            return;
        }

        var target = Path.Combine(localDirectory, targetName);
        if (!source.IsDirectory)
        {
            transfers.Add(DownloadFile(source, target, options, remove, result));
            return;
        }

        IReadOnlyList<RemoteFileInfo> children;
        try
        {
            if (File.Exists(target))
            {
                throw new SessionRemoteException($"Cannot create local folder \"{target}\", a file with the same name exists.");
            }
            Directory.CreateDirectory(target);
            children = FileSystem.ListDirectory(source.FullName);
        }
        catch (Exception ex) when (ex is SessionRemoteException or IOException or UnauthorizedAccessException)
        {
            result.Failures.Add(ToRemoteException(ex));
            return;
        }

        foreach (var child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            DownloadEntry(child, target, child.Name, relativePath + "/" + child.Name, mask, options, remove, transfers, result);
        }

        if (remove)
        {
            try
            {
                if (FileSystem.ListDirectory(source.FullName).Count == 0)
                {
                    FileSystem.DeleteDirectory(source.FullName);
                }
            }
            catch (SessionRemoteException ex)
            {
                result.Failures.Add(ex);
            }
        }
    }

    private TransferEventArgs DownloadFile(RemoteFileInfo source, string localPath, TransferOptions options, bool remove, OperationResultBase result)
    {
        ThrowIfCancelled();
        var args = new TransferEventArgs { FileName = source.FullName, Destination = localPath, Side = ProgressSide.Remote };
        try
        {
            if (Directory.Exists(localPath))
            {
                throw new SessionRemoteException($"Cannot overwrite local folder \"{localPath}\" with a file.");
            }

            var ascii = options.IsAscii(source.Name);
            var existing = File.Exists(localPath) ? new FileInfo(localPath).Length : (long?)null;
            var partialPath = localPath + PartialExtension;
            var plan = PlanTransfer(source.Length, existing, options, ascii,
                () => File.Exists(partialPath) ? new FileInfo(partialPath).Length : null);

            using (var stream = FileSystem.OpenRead(source.FullName, plan.SourceOffset))
            using (var target = OpenLocalWrite(plan.UsePartial ? partialPath : localPath, plan.TargetOffset))
            {
                args.Length = Copy(stream, target, source.Length, plan.SourceOffset, ascii, true, options.SpeedLimit, source.FullName, ProgressSide.Remote);
            }
            args.Resumed = plan.SourceOffset > 0;

            if (plan.UsePartial)
            {
                File.Move(partialPath, localPath, true);
            }
            if (options.PreserveTimestamp && source.LastWriteTimeUtc > DateTime.MinValue)
            {
                TrySetAttribute(args, "timestamp", () => File.SetLastWriteTimeUtc(localPath, source.LastWriteTimeUtc));
            }
            if (remove)
            {
                try
                {
                    FileSystem.DeleteFile(source.FullName);
                }
                catch (SessionRemoteException ex)
                {
                    args.RemovalError = ex;
                    result.Failures.Add(ex);
                }
            }
        }
        catch (Exception ex) when (ex is SessionRemoteException or IOException or UnauthorizedAccessException)
        {
            args.Error = ToRemoteException(ex);
            result.Failures.Add(args.Error);
        }

        OnFileTransferred(args);
        return args;
    }

    #endregion

    #region Synchronization

    public IReadOnlyList<ComparisonDifference> CompareDirectories(SynchronizationMode mode, string localPath, string remotePath,
        bool removeFiles, bool mirror = false, SynchronizationCriteria criteria = SynchronizationCriteria.Time, TransferOptions? options = null)
    {
        if (mode == SynchronizationMode.Both)
        {
            if (removeFiles)
            {
                throw new ArgumentException("Cannot delete files in synchronization mode Both");
            }
            if (mirror)
            {
                throw new ArgumentException("Cannot mirror files in synchronization mode Both");
            }
            if (criteria != SynchronizationCriteria.Time)
            {
                throw new ArgumentException("Only Time criteria is allowed in synchronization mode Both");
            }
        }

        var localRoot = Path.GetFullPath(localPath);
        if (!Directory.Exists(localRoot))
        {
            throw new SessionException($"Local folder \"{localPath}\" does not exist.");
        }
        var remoteRoot = ResolvePath(remotePath);
        if (FileSystem.Stat(remoteRoot) is not { IsDirectory: true })
        {
            throw new SessionRemoteException($"Remote folder \"{remoteRoot}\" does not exist.");
        }

        var differences = new List<ComparisonDifference>();
        var context = new CompareContext(mode, removeFiles, mirror, criteria, FileMask.Parse(options?.FileMask), differences);
        CompareLevel(context, localRoot, remoteRoot, "");
        return differences;
    }

    public SynchronizationResult SynchronizeDirectories(SynchronizationMode mode, string localPath, string remotePath,
        bool removeFiles, bool mirror = false, SynchronizationCriteria criteria = SynchronizationCriteria.Time, TransferOptions? options = null)
    {
        _cancelRequested = false;
        options ??= new TransferOptions();
        var result = new SynchronizationResult();
        var differences = CompareDirectories(mode, localPath, remotePath, removeFiles, mirror, criteria, options);
        var mask = FileMask.Parse(options.FileMask);

        foreach (var difference in differences)
        {
            var name = Path.GetFileName(difference.LocalPath);
            switch (difference.Action)
            {
                case SynchronizationAction.UploadNew:
                case SynchronizationAction.UploadUpdate:
                    UploadEntry(difference.LocalPath, RemotePath.GetDirectoryName(difference.RemotePath), RemotePath.GetFileName(difference.RemotePath),
                        difference.RelativePath, mask, options, false, result.Uploads, result);
                    break;

                case SynchronizationAction.DownloadNew:
                case SynchronizationAction.DownloadUpdate:
                    DownloadEntry(difference.RemoteFile!, Path.GetDirectoryName(difference.LocalPath)!, name,
                        difference.RelativePath, mask, options, false, result.Downloads, result);
                    break;

                case SynchronizationAction.DeleteRemote:
                    RemoveRemote(difference.RemoteFile!, result.Removals, result);
                    break;

                case SynchronizationAction.DeleteLocal:
                    var removal = new RemovalEventArgs { FileName = difference.LocalPath };
                    removal.Error = TryRemoveLocal(difference.LocalPath, difference.IsDirectory, result, recursive: true);
                    result.Removals.Add(removal);
                    FileRemoved?.Invoke(this, removal);
                    break;
            }
        }
        return result;
    }

    private sealed record CompareContext(
        SynchronizationMode Mode, bool RemoveFiles, bool Mirror, SynchronizationCriteria Criteria,
        FileMask Mask, List<ComparisonDifference> Differences);

    private void CompareLevel(CompareContext context, string localDirectory, string remoteDirectory, string relativeDirectory)
    {
        string Relative(string name) => relativeDirectory.Length == 0 ? name : relativeDirectory + "/" + name;

        var locals = new DirectoryInfo(localDirectory).EnumerateFileSystemInfos()
            .Where(i => context.Mask.Matches(i.Name, i is DirectoryInfo, Relative(i.Name)))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var remotes = new Dictionary<string, RemoteFileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var remote in FileSystem.ListDirectory(remoteDirectory).OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            if (context.Mask.Matches(remote.Name, remote.IsDirectory, Relative(remote.Name)))
            {
                remotes.TryAdd(remote.Name, remote);
            }
        }

        var uploads = context.Mode != SynchronizationMode.Local;
        var downloads = context.Mode != SynchronizationMode.Remote;

        foreach (var local in locals)
        {
            var isDirectory = local is DirectoryInfo;
            remotes.Remove(local.Name, out var remote);
            var remotePath = remote?.FullName ?? RemotePath.Combine(remoteDirectory, local.Name);
            var localInfo = new ComparisonFileInfo
            {
                FileName = local.FullName,
                Length = local is FileInfo f ? f.Length : 0,
                LastWriteTimeUtc = local.LastWriteTimeUtc,
            };

            SynchronizationAction? action = null;
            if (remote == null)
            {
                action = uploads ? SynchronizationAction.UploadNew :
                    context.RemoveFiles ? SynchronizationAction.DeleteLocal : null;
            }
            else if (isDirectory && remote.IsDirectory)
            {
                CompareLevel(context, local.FullName, remote.FullName, Relative(local.Name));
            }
            else if (!isDirectory && !remote.IsDirectory)
            {
                action = CompareFiles(context, localInfo, remote);
            }
            else
            {
                Log($"Skipping \"{Relative(local.Name)}\", it is a file on one side and a folder on the other.");
            }

            if (action != null)
            {
                context.Differences.Add(new ComparisonDifference
                {
                    Action = action.Value,
                    IsDirectory = isDirectory,
                    Local = localInfo,
                    Remote = remote == null ? null : ToComparisonInfo(remote),
                    LocalPath = local.FullName,
                    RemotePath = remotePath,
                    RemoteFile = remote,
                    RelativePath = Relative(local.Name),
                });
            }
        }

        // Remote entries without a local counterpart
        foreach (var remote in remotes.Values)
        {
            SynchronizationAction? action = downloads ? SynchronizationAction.DownloadNew :
                context.RemoveFiles ? SynchronizationAction.DeleteRemote : null;
            if (action != null)
            {
                context.Differences.Add(new ComparisonDifference
                {
                    Action = action.Value,
                    IsDirectory = remote.IsDirectory,
                    Remote = ToComparisonInfo(remote),
                    LocalPath = Path.Combine(localDirectory, remote.Name),
                    RemotePath = remote.FullName,
                    RemoteFile = remote,
                    RelativePath = Relative(remote.Name),
                });
            }
        }
    }

    private static SynchronizationAction? CompareFiles(CompareContext context, ComparisonFileInfo local, RemoteFileInfo remote)
    {
        var localNewer = local.LastWriteTimeUtc - remote.LastWriteTimeUtc > TimeTolerance;
        var remoteNewer = remote.LastWriteTimeUtc - local.LastWriteTimeUtc > TimeTolerance;
        var byTime = context.Criteria.HasFlag(SynchronizationCriteria.Time);
        var bySize = context.Criteria.HasFlag(SynchronizationCriteria.Size) && local.Length != remote.Length;

        return context.Mode switch
        {
            SynchronizationMode.Remote when (byTime && (localNewer || (context.Mirror && remoteNewer))) || bySize
                => SynchronizationAction.UploadUpdate,
            SynchronizationMode.Local when (byTime && (remoteNewer || (context.Mirror && localNewer))) || bySize
                => SynchronizationAction.DownloadUpdate,
            SynchronizationMode.Both when localNewer => SynchronizationAction.UploadUpdate,
            SynchronizationMode.Both when remoteNewer => SynchronizationAction.DownloadUpdate,
            _ => null,
        };
    }

    private static ComparisonFileInfo ToComparisonInfo(RemoteFileInfo remote) => new()
    {
        FileName = remote.FullName,
        Length = remote.Length,
        LastWriteTimeUtc = remote.LastWriteTimeUtc,
    };

    #endregion

    #region Helpers

    private readonly record struct TransferPlan(long SourceOffset, long TargetOffset, bool UsePartial);

    /// <summary>
    /// Decides where a transfer starts: Append writes after the existing target, Resume continues
    /// an existing shorter target, Overwrite goes through a ".filepart" temporary file when
    /// resume support applies, continuing a previously interrupted one.
    /// </summary>
    private static TransferPlan PlanTransfer(long sourceLength, long? existingLength, TransferOptions options, bool ascii, Func<long?> partialLength)
    {
        switch (options.OverwriteMode)
        {
            case OverwriteMode.Append:
                return new TransferPlan(0, existingLength ?? 0, false);

            case OverwriteMode.Resume:
                return !ascii && existingLength is { } length && length <= sourceLength
                    ? new TransferPlan(length, length, false)
                    : new TransferPlan(0, 0, false);

            default:
                if (ascii || !options.ResumeSupport.UseTemporaryFile(sourceLength))
                {
                    return new TransferPlan(0, 0, false);
                }
                return partialLength() is { } partial && partial > 0 && partial < sourceLength
                    ? new TransferPlan(partial, partial, true)
                    : new TransferPlan(0, 0, true);
        }
    }

    private long Copy(Stream source, Stream target, long total, long offset, bool ascii, bool toCrLf,
        int speedLimit, string fileName, ProgressSide side)
    {
        var bufferSize = speedLimit > 0 ? Math.Clamp(speedLimit * 1024 / 4, 1024, BufferSize) : BufferSize;
        var buffer = new byte[bufferSize];
        var converter = ascii ? new EolConverter(toCrLf) : null;
        var converted = ascii ? new byte[bufferSize * 2] : null;

        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        long transferred = offset;
        long sessionBytes = 0;

        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ThrowIfCancelled();
            if (converter != null)
            {
                target.Write(converted!, 0, converter.Convert(buffer.AsSpan(0, read), converted));
            }
            else
            {
                target.Write(buffer, 0, read);
            }
            transferred += read;
            sessionBytes += read;

            if (speedLimit > 0)
            {
                var ahead = sessionBytes / (speedLimit * 1024.0) - stopwatch.Elapsed.TotalSeconds;
                if (ahead > 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(ahead));
                }
            }

            if (stopwatch.Elapsed - lastReport >= ProgressInterval)
            {
                lastReport = stopwatch.Elapsed;
                ReportProgress(fileName, side, transferred, total, sessionBytes, stopwatch.Elapsed);
            }
        }

        if (converter != null)
        {
            target.Write(converted!, 0, converter.Flush(converted));
        }
        target.Flush();
        ReportProgress(fileName, side, transferred, total, sessionBytes, stopwatch.Elapsed);
        return transferred;
    }

    private void ReportProgress(string fileName, ProgressSide side, long transferred, long total, long sessionBytes, TimeSpan elapsed) =>
        FileTransferProgress?.Invoke(this, new FileTransferProgressEventArgs
        {
            FileName = fileName,
            Side = side,
            Transferred = transferred,
            Total = total,
            CPS = elapsed.TotalSeconds > 0 ? (long)(sessionBytes / elapsed.TotalSeconds) : 0,
        });

    private static FileStream OpenLocalRead(string path, long offset)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }
        return stream;
    }

    private static FileStream OpenLocalWrite(string path, long offset)
    {
        if (offset == 0)
        {
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
        }
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, BufferSize);
        stream.Seek(offset, SeekOrigin.Begin);
        return stream;
    }

    private List<string> ResolveLocalSources(string localPath, OperationResultBase result)
    {
        var fullPath = Path.GetFullPath(localPath);
        if (Path.GetPathRoot(fullPath) != fullPath)
        {
            fullPath = Path.TrimEndingDirectorySeparator(fullPath);
        }

        var name = Path.GetFileName(fullPath);
        if (FileMask.HasWildcard(name))
        {
            var directory = Path.GetDirectoryName(fullPath)!;
            var regex = FileMask.WildcardToRegex(name);
            var matches = Directory.Exists(directory)
                ? Directory.EnumerateFileSystemEntries(directory)
                    .Where(p => regex.IsMatch(Path.GetFileName(p)))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
            if (matches.Count == 0)
            {
                result.Failures.Add(new SessionRemoteException($"No file matching \"{name}\" found."));
            }
            return matches;
        }

        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            return [fullPath];
        }
        result.Failures.Add(new SessionRemoteException($"File or folder \"{localPath}\" does not exist."));
        return [];
    }

    private List<RemoteFileInfo> ResolveRemoteSources(string remotePath, OperationResultBase result)
    {
        var name = RemotePath.GetFileName(remotePath);
        if (FileMask.HasWildcard(name))
        {
            var regex = FileMask.WildcardToRegex(name);
            var matches = FileSystem.ListDirectory(RemotePath.GetDirectoryName(remotePath))
                .Where(f => regex.IsMatch(f.Name))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToList();
            if (matches.Count == 0)
            {
                result.Failures.Add(new SessionRemoteException($"No file matching \"{name}\" found."));
            }
            return matches;
        }

        var info = FileSystem.Stat(remotePath);
        if (info == null)
        {
            result.Failures.Add(new SessionRemoteException($"File or folder \"{remotePath}\" does not exist."));
            return [];
        }
        return [info];
    }

    /// <summary>
    /// A target ending with '/' or naming an existing folder receives files under their own names,
    /// otherwise its last segment is an operation mask such as "new.txt" or "*.bak".
    /// </summary>
    private (string Directory, string OperationMask) ResolveRemoteTarget(string remotePath)
    {
        var resolved = ResolvePath(remotePath);
        if (remotePath.Length == 0 || remotePath.EndsWith('/') || FileSystem.Stat(resolved) is { IsDirectory: true })
        {
            return (resolved, "*");
        }
        return (RemotePath.GetDirectoryName(resolved), RemotePath.GetFileName(resolved));
    }

    private static (string Directory, string OperationMask) ResolveLocalTarget(string localPath)
    {
        var fullPath = Path.GetFullPath(localPath.Length == 0 ? "." : localPath);
        if (Path.EndsInDirectorySeparator(localPath) || Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
            return (fullPath, "*");
        }
        return (Path.GetDirectoryName(fullPath)!, Path.GetFileName(fullPath));
    }

    private void RemoveRemote(RemoteFileInfo file, List<RemovalEventArgs> removals, OperationResultBase result)
    {
        var args = new RemovalEventArgs { FileName = file.FullName };
        try
        {
            if (file.IsDirectory)
            {
                foreach (var child in FileSystem.ListDirectory(file.FullName))
                {
                    RemoveRemote(child, removals, result);
                }
                FileSystem.DeleteDirectory(file.FullName);
            }
            else
            {
                FileSystem.DeleteFile(file.FullName);
            }
        }
        catch (SessionRemoteException ex)
        {
            args.Error = ex;
            result.Failures.Add(ex);
        }
        removals.Add(args);
        FileRemoved?.Invoke(this, args);
    }

    private static SessionRemoteException? TryRemoveLocal(string path, bool isDirectory, OperationResultBase result, bool recursive = false)
    {
        try
        {
            if (isDirectory)
            {
                Directory.Delete(path, recursive);
            }
            else
            {
                File.Delete(path);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var error = new SessionRemoteException($"Cannot delete \"{path}\": {ex.Message}", ex);
            result.Failures.Add(error);
            return error;
        }
    }

    private void TrySetAttribute(TransferEventArgs args, string attribute, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is SessionRemoteException or IOException or UnauthorizedAccessException)
        {
            var warning = $"Transfer of \"{args.FileName}\" succeeded, but setting {attribute} failed: {ex.Message}";
            args.Warnings.Add(warning);
            Log(warning);
        }
    }

    private static SessionRemoteException ToRemoteException(Exception ex) =>
        ex as SessionRemoteException ?? new SessionRemoteException(ex.Message, ex);

    private void OnFileTransferred(TransferEventArgs args)
    {
        Log(args.ToString());
        FileTransferred?.Invoke(this, args);
    }

    private void Log(string message) => OutputDataReceived?.Invoke(this, message);

    #endregion
}
