namespace EllediTransfer;

public class SessionException : Exception
{
    public SessionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public class SessionRemoteException : SessionException
{
    public SessionRemoteException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>The server refused the operation for lack of permissions (e.g. writing a root-owned file).</summary>
    public bool IsPermissionDenied =>
        InnerException is Renci.SshNet.Common.SftpPermissionDeniedException ||
        Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The server presented a host key that does not match <see cref="SessionOptions.SshHostKeyFingerprint"/>.</summary>
public sealed class HostKeyMismatchException : SessionRemoteException
{
    public HostKeyMismatchException(string message, string actualFingerprint, Exception? innerException = null)
        : base(message, innerException)
    {
        ActualFingerprint = actualFingerprint;
    }

    public string ActualFingerprint { get; }
}

public enum ProgressSide
{
    Local,
    Remote,
}

public sealed class RemoteFileInfo
{
    public required string FullName { get; init; }
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public long Length { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
    public FilePermissions? FilePermissions { get; init; }

    public override string ToString() =>
        $"{(IsDirectory ? "D" : "-")}{FilePermissions?.Octal ?? "---",4} {Length,12} {LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} {Name}";
}

public sealed class TransferEventArgs : EventArgs
{
    public required string FileName { get; init; }
    public required string Destination { get; init; }
    public ProgressSide Side { get; init; }
    public long Length { get; internal set; }
    public bool Resumed { get; internal set; }
    public SessionRemoteException? Error { get; internal set; }
    public SessionRemoteException? RemovalError { get; internal set; }
    /// <summary>Non-fatal problems, e.g. the server refused to set timestamp or permissions.</summary>
    public List<string> Warnings { get; } = [];

    public override string ToString() => Error == null
        ? $"{FileName} => {Destination} ({Length} B{(Resumed ? ", resumed" : "")})"
        : $"{FileName}: {Error.Message}";
}

public sealed class RemovalEventArgs : EventArgs
{
    public required string FileName { get; init; }
    public SessionRemoteException? Error { get; internal set; }
}

public sealed class FileTransferProgressEventArgs : EventArgs
{
    public required string FileName { get; init; }
    public ProgressSide Side { get; init; }
    public long Transferred { get; init; }
    public long Total { get; init; }
    public double FileProgress => Total > 0 ? Math.Min(1.0, (double)Transferred / Total) : 1.0;
    /// <summary>Bytes per second.</summary>
    public long CPS { get; init; }
}

public abstract class OperationResultBase
{
    public List<SessionRemoteException> Failures { get; } = [];
    public bool IsSuccess => Failures.Count == 0;

    public void Check()
    {
        if (!IsSuccess)
        {
            throw Failures[0];
        }
    }
}

public sealed class TransferOperationResult : OperationResultBase
{
    public List<TransferEventArgs> Transfers { get; } = [];
}

public sealed class RemovalOperationResult : OperationResultBase
{
    public List<RemovalEventArgs> Removals { get; } = [];
}

public enum SynchronizationMode
{
    /// <summary>Remote to local.</summary>
    Local,
    /// <summary>Local to remote.</summary>
    Remote,
    Both,
}

[Flags]
public enum SynchronizationCriteria
{
    None = 0,
    Time = 1,
    Size = 2,
    Either = Time | Size,
}

public enum SynchronizationAction
{
    UploadNew,
    UploadUpdate,
    DownloadNew,
    DownloadUpdate,
    DeleteRemote,
    DeleteLocal,
}

public sealed class ComparisonFileInfo
{
    public required string FileName { get; init; }
    public long Length { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
}

public sealed class ComparisonDifference
{
    public SynchronizationAction Action { get; init; }
    public bool IsDirectory { get; init; }
    public ComparisonFileInfo? Local { get; init; }
    public ComparisonFileInfo? Remote { get; init; }

    public required string LocalPath { get; init; }
    public required string RemotePath { get; init; }
    internal RemoteFileInfo? RemoteFile { get; init; }
    public string RelativePath { get; init; } = "";

    public override string ToString()
    {
        var (from, arrow, to) = Action switch
        {
            SynchronizationAction.UploadNew or SynchronizationAction.UploadUpdate => (LocalPath, "=>", RemotePath),
            SynchronizationAction.DownloadNew or SynchronizationAction.DownloadUpdate => (RemotePath, "=>", LocalPath),
            SynchronizationAction.DeleteRemote => ("", "x", RemotePath),
            _ => ("", "x", LocalPath),
        };
        var parts = new[] { Action.ToString().PadRight(15), IsDirectory ? "[dir]" : "", from, arrow, to };
        return string.Join(" ", parts.Where(p => p.Length > 0));
    }
}

public sealed class SynchronizationResult : OperationResultBase
{
    public List<TransferEventArgs> Uploads { get; } = [];
    public List<TransferEventArgs> Downloads { get; } = [];
    public List<RemovalEventArgs> Removals { get; } = [];
}
