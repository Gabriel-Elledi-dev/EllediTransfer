using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using EllediTransfer.FileSystems;

namespace EllediTransfer;

/// <summary>
/// Writes a file the session user may not modify, using another account that can use sudo (or root):
/// the content goes to a private temporary file in /tmp, then <c>sudo</c> copies it over the target with
/// <c>cat &gt;</c>, so the target keeps its owner and permissions. The password reaches sudo through
/// stdin only, never on a command line.
/// </summary>
public static class ElevatedSave
{
    public static void Save(SessionOptions connection, string userName, string password, string localPath, string remotePath)
    {
        if (connection.Protocol != Protocol.Sftp)
        {
            throw new SessionException("Saving with sudo needs an SSH (SFTP) connection.");
        }

        // Same server and verified host key, different account
        var options = new SessionOptions
        {
            HostName = connection.HostName,
            PortNumber = connection.PortNumber,
            Timeout = connection.Timeout,
            SshHostKeyFingerprint = connection.SshHostKeyFingerprint,
            GiveUpSecurityAndAcceptAnySshHostKey = connection.GiveUpSecurityAndAcceptAnySshHostKey,
            UserName = userName,
            Password = password,
        };
        var temporary = $"/tmp/.elleditransfer-{Guid.NewGuid():N}";

        using var sftp = new SftpClient(SshConnection.CreateConnectionInfo(options));
        using var ssh = new SshClient(SshConnection.CreateConnectionInfo(options));
        try
        {
            SshConnection.Connect(sftp, options);
            using (var source = File.OpenRead(localPath))
            using (var target = sftp.Open(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                source.CopyTo(target);
            }
            sftp.ChangePermissions(temporary, 600);

            SshConnection.Connect(ssh, options);
            var copy = $"sh -c 'cat -- \"$1\" > \"$2\"; s=$?; rm -f -- \"$1\"; exit $s' sh {Quote(temporary)} {Quote(remotePath)}";
            var isRoot = userName == "root";
            using var command = ssh.CreateCommand(isRoot ? copy : $"sudo -S -p '' -- {copy}");
            command.CommandTimeout = TimeSpan.FromMinutes(5);
            var running = command.BeginExecute();
            if (!isRoot)
            {
                using var input = command.CreateInputStream();
                var line = Encoding.UTF8.GetBytes(password + "\n");
                input.Write(line, 0, line.Length);
            }
            command.EndExecute(running);

            if (command.ExitStatus != 0)
            {
                throw new SessionRemoteException(DescribeFailure(userName, command.Error));
            }
        }
        catch (SshAuthenticationException ex)
        {
            throw new SessionRemoteException($"The server refused user \"{userName}\" with this password.", ex);
        }
        catch (Exception ex) when (ex is SshException or IOException)
        {
            throw new SessionRemoteException($"Saving with sudo failed: {ex.Message}", ex);
        }
        finally
        {
            // Never leave the copy behind, even if sudo refused
            try
            {
                if (sftp.IsConnected && sftp.Exists(temporary))
                {
                    sftp.DeleteFile(temporary);
                }
            }
            catch (Exception ex) when (ex is SshException or IOException)
            {
            }
        }
    }

    private static string DescribeFailure(string userName, string error)
    {
        var text = error.Trim();
        if (text.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Sorry, try again", StringComparison.OrdinalIgnoreCase))
        {
            return "sudo did not accept the password.";
        }
        if (text.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not allowed", StringComparison.OrdinalIgnoreCase))
        {
            return $"User \"{userName}\" is not allowed to use sudo on this server.";
        }
        return text.Length > 0 ? text : "The copy with sudo did not succeed.";
    }

    /// <summary>Single-quotes a value for the POSIX shell.</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
