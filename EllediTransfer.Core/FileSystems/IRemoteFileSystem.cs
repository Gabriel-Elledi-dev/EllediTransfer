namespace EllediTransfer.FileSystems;

/// <summary>
/// Protocol-specific remote operations used by the transfer engine.
/// Paths are absolute, '/'-separated remote paths.
/// </summary>
internal interface IRemoteFileSystem : IDisposable
{
    /// <summary>Host key (SSH) or certificate (TLS) fingerprint seen while connecting, if any.</summary>
    string? ServerFingerprint { get; }

    void Open();
    void Close();

    string GetCurrentDirectory();
    void ChangeDirectory(string path);

    IReadOnlyList<RemoteFileInfo> ListDirectory(string path);
    RemoteFileInfo? Stat(string path);

    Stream OpenRead(string path, long offset);

    /// <summary>
    /// Opens the file for writing. <paramref name="offset"/> 0 creates or truncates the file,
    /// a positive offset must equal the current remote length and appends to it.
    /// </summary>
    Stream OpenWrite(string path, long offset);

    void DeleteFile(string path);
    void DeleteDirectory(string path);
    void CreateDirectory(string path);
    void Rename(string sourcePath, string targetPath);
    void SetLastWriteTime(string path, DateTime lastWriteTimeUtc);
    void SetPermissions(string path, FilePermissions permissions);
}
