using System.Globalization;
using System.Security.Cryptography;
using FluentFTP;
using FluentFTP.Exceptions;

namespace EllediTransfer.FileSystems;

internal sealed class FtpFileSystem(SessionOptions options) : IRemoteFileSystem
{
    private FtpClient? _client;

    public string? ServerFingerprint { get; private set; }

    private FtpClient Client => _client ?? throw new InvalidOperationException("Session is not opened");

    public void Open()
    {
        _client = new FtpClient(
            options.HostName,
            options.UserName ?? "anonymous",
            options.Password ?? "anonymous@example.com",
            options.EffectivePort);

        var timeout = (int)options.Timeout.TotalMilliseconds;
        _client.Config.ConnectTimeout = timeout;
        _client.Config.ReadTimeout = timeout;
        _client.Config.DataConnectionConnectTimeout = timeout;
        _client.Config.DataConnectionReadTimeout = timeout;
        _client.Config.TimeConversion = FtpDate.UTC;
        _client.Config.DataConnectionType = options.FtpPassive ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive;
        _client.Config.EncryptionMode = options.FtpSecure switch
        {
            FtpSecure.Implicit => FtpEncryptionMode.Implicit,
            FtpSecure.Explicit => FtpEncryptionMode.Explicit,
            _ => FtpEncryptionMode.None,
        };

        _client.ValidateCertificate += (_, e) =>
        {
            ServerFingerprint = string.Join(":", e.Certificate.GetCertHash(HashAlgorithmName.SHA256).Select(b => b.ToString("x2")));
            e.Accept =
                options.GiveUpSecurityAndAcceptAnyTlsHostCertificate ||
                (string.IsNullOrEmpty(options.TlsHostCertificateFingerprint)
                    ? e.PolicyErrors == System.Net.Security.SslPolicyErrors.None
                    : CertificateMatches(ServerFingerprint));
        };

        try
        {
            _client.Connect();
        }
        catch (Exception ex) when (ex is FtpException or IOException or System.Net.Sockets.SocketException or TimeoutException or System.Security.Authentication.AuthenticationException)
        {
            var detail = ServerFingerprint != null && ex is System.Security.Authentication.AuthenticationException
                ? $" Certificate fingerprint is {ServerFingerprint}."
                : "";
            throw new SessionRemoteException($"Connection to {options.HostName} failed: {ex.Message}{detail}", ex);
        }
    }

    private bool CertificateMatches(string actual) =>
        options.TlsHostCertificateFingerprint!
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(expected => string.Equals(expected.Replace("-", ":"), actual, StringComparison.OrdinalIgnoreCase));

    public void Close() => _client?.Disconnect();

    public string GetCurrentDirectory() => Wrap(() => Client.GetWorkingDirectory(), ".");

    public void ChangeDirectory(string path) => Wrap(() => Client.SetWorkingDirectory(path), path);

    public IReadOnlyList<RemoteFileInfo> ListDirectory(string path) =>
        Wrap(() => Client.GetListing(path)
            .Where(i => i.Name is not "." and not ".." && i.Type != FtpObjectType.Link)
            .Select(i => ToRemoteFileInfo(i, RemotePath.Combine(path, i.Name)))
            .ToList(), path);

    public RemoteFileInfo? Stat(string path)
    {
        try
        {
            var item = Client.GetObjectInfo(path);
            if (item != null)
            {
                return ToRemoteFileInfo(item, path);
            }
        }
        catch (FtpCommandException)
        {
            // MLST not supported, fall back below
        }

        var name = RemotePath.GetFileName(path);
        if (Client.DirectoryExists(path))
        {
            return new RemoteFileInfo { FullName = path, Name = name, IsDirectory = true };
        }
        if (Client.FileExists(path))
        {
            return new RemoteFileInfo
            {
                FullName = path,
                Name = name,
                Length = Client.GetFileSize(path),
                LastWriteTimeUtc = Client.GetModifiedTime(path),
            };
        }
        return null;
    }

    public Stream OpenRead(string path, long offset) =>
        Wrap(() => Client.OpenRead(path, FtpDataType.Binary, offset, false), path);

    public Stream OpenWrite(string path, long offset) =>
        Wrap(() => offset == 0
            ? Client.OpenWrite(path, FtpDataType.Binary, false)
            : Client.OpenAppend(path, FtpDataType.Binary, false), path);

    public void DeleteFile(string path) => Wrap(() => Client.DeleteFile(path), path);

    public void DeleteDirectory(string path) => Wrap(() => Client.DeleteDirectory(path), path);

    public void CreateDirectory(string path) => Wrap(() => Client.CreateDirectory(path, false), path);

    public void Rename(string sourcePath, string targetPath) => Wrap(() => Client.Rename(sourcePath, targetPath), sourcePath);

    public void SetLastWriteTime(string path, DateTime lastWriteTimeUtc) =>
        Wrap(() => Client.SetModifiedTime(path, lastWriteTimeUtc), path);

    // FluentFTP takes the octal digits as a decimal number (e.g. 644)
    public void SetPermissions(string path, FilePermissions permissions) =>
        Wrap(() => Client.Chmod(path, int.Parse(permissions.Octal, CultureInfo.InvariantCulture)), path);

    public void Dispose() => _client?.Dispose();

    private static RemoteFileInfo ToRemoteFileInfo(FtpListItem item, string fullName) => new()
    {
        FullName = fullName,
        Name = RemotePath.GetFileName(fullName),
        IsDirectory = item.Type == FtpObjectType.Directory,
        Length = item.Type == FtpObjectType.Directory ? 0 : Math.Max(0, item.Size),
        LastWriteTimeUtc = DateTime.SpecifyKind(item.Modified, DateTimeKind.Utc),
        FilePermissions = item.Chmod > 0
            ? new FilePermissions(Convert.ToInt32(item.Chmod.ToString(CultureInfo.InvariantCulture), 8))
            : null,
    };

    private static void Wrap(Action action, string path) => Wrap(() => { action(); return 0; }, path);

    private static T Wrap<T>(Func<T> action, string path)
    {
        try
        {
            return action();
        }
        catch (FtpException ex)
        {
            var message = ex.InnerException != null ? $"{ex.Message} {ex.InnerException.Message}" : ex.Message;
            throw new SessionRemoteException($"Error on \"{path}\": {message}", ex);
        }
    }
}
