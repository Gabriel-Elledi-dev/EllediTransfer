using Renci.SshNet;
using Renci.SshNet.Common;

namespace EllediTransfer.FileSystems;

/// <summary>
/// Authentication and host key verification shared by every SSH-based client (SFTP, terminal).
/// </summary>
internal static class SshConnection
{
    public static ConnectionInfo CreateConnectionInfo(SessionOptions options)
    {
        var userName = options.UserName ?? Environment.UserName;
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(options.SshPrivateKeyPath))
        {
            var keyFile = string.IsNullOrEmpty(options.PrivateKeyPassphrase)
                ? new PrivateKeyFile(options.SshPrivateKeyPath)
                : new PrivateKeyFile(options.SshPrivateKeyPath, options.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(userName, keyFile));
        }
        if (options.Password != null)
        {
            methods.Add(new PasswordAuthenticationMethod(userName, options.Password));
            var keyboardInteractive = new KeyboardInteractiveAuthenticationMethod(userName);
            keyboardInteractive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = options.Password;
                }
            };
            methods.Add(keyboardInteractive);
        }
        if (methods.Count == 0)
        {
            methods.Add(new NoneAuthenticationMethod(userName));
        }

        return new ConnectionInfo(options.HostName, options.EffectivePort, userName, methods.ToArray())
        {
            Timeout = options.Timeout,
        };
    }

    /// <summary>
    /// Connects <paramref name="client"/>, accepting the server only if its host key matches
    /// <see cref="SessionOptions.SshHostKeyFingerprint"/>. Returns the server fingerprint.
    /// </summary>
    public static string? Connect(BaseClient client, SessionOptions options)
    {
        string? fingerprint = null;
        var hostKeyMatched = false;
        client.HostKeyReceived += (_, e) =>
        {
            fingerprint = $"{e.HostKeyName} {e.KeyLength} SHA256:{e.FingerPrintSHA256}";
            hostKeyMatched = HostKeyMatches(options, e);
            e.CanTrust = hostKeyMatched || options.GiveUpSecurityAndAcceptAnySshHostKey;
        };

        try
        {
            client.Connect();
            return fingerprint;
        }
        catch (SshConnectionException ex) when (!hostKeyMatched && fingerprint != null && !options.GiveUpSecurityAndAcceptAnySshHostKey)
        {
            throw new HostKeyMismatchException(
                $"Host key does not match configured key fingerprint \"{options.SshHostKeyFingerprint}\". " +
                $"Host key fingerprint is {fingerprint}.", fingerprint, ex);
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException)
        {
            throw new SessionRemoteException($"Connection to {options.HostName} failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Accepts the WinSCP fingerprint formats "ssh-ed25519 255 SHA256:base64", "SHA256:base64",
    /// a bare SHA-256 base64 value or an MD5 "xx:xx:..." value; several may be separated by ';'.
    /// </summary>
    private static bool HostKeyMatches(SessionOptions options, HostKeyEventArgs e)
    {
        if (string.IsNullOrEmpty(options.SshHostKeyFingerprint))
        {
            return false;
        }

        foreach (var candidate in options.SshHostKeyFingerprint.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
            if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            {
                value = value[7..];
            }
            if (string.Equals(value.TrimEnd('='), e.FingerPrintSHA256.TrimEnd('='), StringComparison.Ordinal))
            {
                return true;
            }
            if (value.Contains(':') && string.Equals(value, e.FingerPrintMD5, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
