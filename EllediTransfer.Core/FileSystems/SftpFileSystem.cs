using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace EllediTransfer.FileSystems;

internal sealed class SftpFileSystem(SessionOptions options) : IRemoteFileSystem
{
    private SftpClient? _client;

    public string? ServerFingerprint { get; private set; }

    private SftpClient Client => _client ?? throw new InvalidOperationException("Session is not opened");

    public void Open()
    {
        _client = new SftpClient(SshConnection.CreateConnectionInfo(options));
        ServerFingerprint = SshConnection.Connect(_client, options);
    }

    public void Close() => _client?.Disconnect();

    public string GetCurrentDirectory() => Client.WorkingDirectory;

    public void ChangeDirectory(string path) => Wrap(() => Client.ChangeDirectory(path), path);

    public IReadOnlyList<RemoteFileInfo> ListDirectory(string path) =>
        Wrap(() => Client.ListDirectory(path)
            .Where(f => f.Name is not "." and not "..")
            .Select(f => ToRemoteFileInfo(f, RemotePath.Combine(path, f.Name)))
            .ToList(), path);

    public RemoteFileInfo? Stat(string path)
    {
        try
        {
            return ToRemoteFileInfo(Client.Get(path), path);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    public Stream OpenRead(string path, long offset) => Wrap(() =>
    {
        var stream = Client.Open(path, FileMode.Open, FileAccess.Read);
        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }
        return (Stream)stream;
    }, path);

    public Stream OpenWrite(string path, long offset) => Wrap(() =>
    {
        if (offset == 0)
        {
            return (Stream)Client.Open(path, FileMode.Create, FileAccess.Write);
        }
        var stream = Client.Open(path, FileMode.OpenOrCreate, FileAccess.Write);
        stream.Seek(offset, SeekOrigin.Begin);
        return stream;
    }, path);

    public void DeleteFile(string path) => Wrap(() => Client.DeleteFile(path), path);

    public void DeleteDirectory(string path) => Wrap(() => Client.DeleteDirectory(path), path);

    public void CreateDirectory(string path) => Wrap(() => Client.CreateDirectory(path), path);

    public void Rename(string sourcePath, string targetPath) => Wrap(() => Client.RenameFile(sourcePath, targetPath), sourcePath);

    public void SetLastWriteTime(string path, DateTime lastWriteTimeUtc) => Wrap(() =>
    {
        var attributes = Client.GetAttributes(path);
        attributes.LastWriteTimeUtc = lastWriteTimeUtc;
        Client.SetAttributes(path, attributes);
    }, path);

    // SSH.NET reads the decimal digits of the value as octal digits (e.g. 644)
    public void SetPermissions(string path, FilePermissions permissions) =>
        Wrap(() => Client.ChangePermissions(path, short.Parse(permissions.Octal)), path);

    public void Dispose() => _client?.Dispose();

    private static RemoteFileInfo ToRemoteFileInfo(ISftpFile file, string fullName) => new()
    {
        FullName = fullName,
        Name = RemotePath.GetFileName(fullName),
        IsDirectory = file.IsDirectory,
        Length = file.IsDirectory ? 0 : file.Length,
        LastWriteTimeUtc = file.LastWriteTimeUtc,
        FilePermissions = new FilePermissions(
            (file.OwnerCanRead ? 0x100 : 0) | (file.OwnerCanWrite ? 0x80 : 0) | (file.OwnerCanExecute ? 0x40 : 0) |
            (file.GroupCanRead ? 0x20 : 0) | (file.GroupCanWrite ? 0x10 : 0) | (file.GroupCanExecute ? 0x8 : 0) |
            (file.OthersCanRead ? 0x4 : 0) | (file.OthersCanWrite ? 0x2 : 0) | (file.OthersCanExecute ? 0x1 : 0)),
    };

    private static void Wrap(Action action, string path) => Wrap(() => { action(); return 0; }, path);

    private static T Wrap<T>(Func<T> action, string path)
    {
        try
        {
            return action();
        }
        catch (SshException ex)
        {
            throw new SessionRemoteException($"Error on \"{path}\": {ex.Message}", ex);
        }
    }
}
