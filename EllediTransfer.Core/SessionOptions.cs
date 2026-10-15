namespace EllediTransfer;

public enum Protocol
{
    Sftp,
    Ftp,
}

public enum FtpSecure
{
    None,
    Implicit,
    Explicit,
}

public sealed class SessionOptions
{
    /// <summary>
    /// Fingerprint that never matches: connecting with it stops at key exchange, before credentials
    /// are sent, and throws <see cref="HostKeyMismatchException"/> carrying the server's real key.
    /// </summary>
    public const string UnknownHostKey = "SHA256:<unknown>";

    public Protocol Protocol { get; set; } = Protocol.Sftp;
    public string HostName { get; set; } = "";
    public int PortNumber { get; set; }
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    // SSH
    public string? SshHostKeyFingerprint { get; set; }
    public bool GiveUpSecurityAndAcceptAnySshHostKey { get; set; }
    public string? SshPrivateKeyPath { get; set; }
    public string? PrivateKeyPassphrase { get; set; }

    // FTP
    public FtpSecure FtpSecure { get; set; } = FtpSecure.None;
    public bool FtpPassive { get; set; } = true;
    public string? TlsHostCertificateFingerprint { get; set; }
    public bool GiveUpSecurityAndAcceptAnyTlsHostCertificate { get; set; }

    public int EffectivePort =>
        PortNumber > 0 ? PortNumber :
        Protocol == Protocol.Sftp ? 22 :
        FtpSecure == FtpSecure.Implicit ? 990 : 21;

    /// <summary>
    /// Parses a session URL such as <c>sftp://user:password@host:22/</c>,
    /// <c>ftp://</c>, <c>ftps://</c> (implicit TLS) or <c>ftpes://</c> (explicit TLS).
    /// </summary>
    public static SessionOptions ParseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid session URL \"{url}\".", nameof(url));
        }

        var options = new SessionOptions { HostName = uri.Host, PortNumber = uri.IsDefaultPort ? 0 : uri.Port };
        switch (uri.Scheme.ToLowerInvariant())
        {
            case "sftp":
                options.Protocol = Protocol.Sftp;
                break;
            case "ftp":
                options.Protocol = Protocol.Ftp;
                break;
            case "ftps":
                options.Protocol = Protocol.Ftp;
                options.FtpSecure = FtpSecure.Implicit;
                break;
            case "ftpes":
                options.Protocol = Protocol.Ftp;
                options.FtpSecure = FtpSecure.Explicit;
                break;
            default:
                throw new ArgumentException($"{uri.Scheme}:// is not supported", nameof(url));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            options.UserName = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1)
            {
                // WinSCP URLs may carry ";fingerprint=..." after the user name
                var password = parts[1];
                var fingerprintIndex = password.IndexOf(";fingerprint=", StringComparison.OrdinalIgnoreCase);
                if (fingerprintIndex >= 0)
                {
                    options.SshHostKeyFingerprint = Uri.UnescapeDataString(password[(fingerprintIndex + 13)..]);
                    password = password[..fingerprintIndex];
                }
                options.Password = Uri.UnescapeDataString(password);
            }
        }

        return options;
    }

    internal void Validate()
    {
        if (string.IsNullOrEmpty(HostName))
        {
            throw new ArgumentException("SessionOptions.HostName is not set.");
        }
        if (!string.IsNullOrEmpty(Password) && string.IsNullOrEmpty(UserName))
        {
            throw new ArgumentException("SessionOptions.Password is set, but SessionOptions.UserName is not.");
        }
        if (Protocol == Protocol.Sftp)
        {
            if (string.IsNullOrEmpty(SshHostKeyFingerprint) && !GiveUpSecurityAndAcceptAnySshHostKey)
            {
                throw new ArgumentException(
                    "SessionOptions.Protocol is Protocol.Sftp, but SessionOptions.SshHostKeyFingerprint is not set.");
            }
            if (FtpSecure != FtpSecure.None)
            {
                throw new ArgumentException("SessionOptions.FtpSecure is not FtpSecure.None, but SessionOptions.Protocol is not Protocol.Ftp.");
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(SshHostKeyFingerprint) || !string.IsNullOrEmpty(SshPrivateKeyPath))
            {
                throw new ArgumentException(
                    "SessionOptions.SshHostKeyFingerprint or SessionOptions.SshPrivateKeyPath is set, but SessionOptions.Protocol is not Protocol.Sftp.");
            }
            if (FtpSecure == FtpSecure.None &&
                (!string.IsNullOrEmpty(TlsHostCertificateFingerprint) || GiveUpSecurityAndAcceptAnyTlsHostCertificate))
            {
                throw new ArgumentException(
                    "SessionOptions.TlsHostCertificateFingerprint or SessionOptions.GiveUpSecurityAndAcceptAnyTlsHostCertificate is set, but SessionOptions.FtpSecure is not enabled.");
            }
        }
    }

    public override string ToString()
    {
        var scheme = Protocol == Protocol.Sftp ? "sftp" :
            FtpSecure == FtpSecure.Implicit ? "ftps" :
            FtpSecure == FtpSecure.Explicit ? "ftpes" : "ftp";
        var user = string.IsNullOrEmpty(UserName) ? "" : Uri.EscapeDataString(UserName) + (string.IsNullOrEmpty(Password) ? "" : ":***") + "@";
        return $"{scheme}://{user}{HostName}:{EffectivePort}/";
    }
}
