using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using EllediTransfer.FileSystems;

namespace EllediTransfer.Terminal;

/// <summary>
/// An interactive shell on an SSH server with an xterm pseudo-terminal, as PuTTY opens it.
/// Output is decoded into <see cref="Screen"/>; input is sent with <see cref="Send(string)"/>.
/// </summary>
public sealed class SshShell : IDisposable
{
    public const string TerminalType = "xterm";

    private readonly SessionOptions _options;
    private readonly object _writeLock = new();
    private SshClient? _client;
    private ShellStream? _stream;
    private Thread? _reader;

    public SshShell(SessionOptions options, int columns, int rows, int scrollbackLines = 5000)
    {
        if (options.Protocol != Protocol.Sftp)
        {
            throw new ArgumentException("A terminal needs an SSH connection (SFTP protocol).", nameof(options));
        }
        _options = options;
        Screen = new TerminalScreen(columns, rows, scrollbackLines);
        Parser = new VtParser(Screen);
        Parser.Reply += Send;
    }

    public TerminalScreen Screen { get; }
    public VtParser Parser { get; }
    public string? ServerFingerprint { get; private set; }
    public bool IsConnected => _stream != null && _client?.IsConnected == true;

    /// <summary>The shell ended (logout, connection lost). Raised on a background thread.</summary>
    public event Action<string?>? Closed;

    /// <summary>Connects and starts the shell; blocks until the shell is ready.</summary>
    public void Connect()
    {
        _client = new SshClient(SshConnection.CreateConnectionInfo(_options))
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        };
        ServerFingerprint = SshConnection.Connect(_client, _options);

        int columns, rows;
        lock (Screen.SyncRoot)
        {
            columns = Screen.Columns;
            rows = Screen.Rows;
        }
        try
        {
            _stream = _client.CreateShellStream(TerminalType, (uint)columns, (uint)rows, 0, 0, 64 * 1024);
        }
        catch (SshException ex)
        {
            throw new SessionRemoteException($"The server refused to open a shell: {ex.Message}", ex);
        }

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "SSH terminal reader" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        var buffer = new byte[32 * 1024];
        string? reason = null;
        try
        {
            int read;
            while (_stream != null && (read = _stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                Parser.Feed(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException)
        {
            reason = ex.Message;
        }
        Closed?.Invoke(reason);
    }

    public void Send(string text) => Send(Encoding.UTF8.GetBytes(text));

    public void Send(byte[] data)
    {
        var stream = _stream;
        if (stream == null || data.Length == 0)
        {
            return;
        }
        try
        {
            lock (_writeLock)
            {
                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
        }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException)
        {
            // The reader loop reports the closed connection
        }
    }

    /// <summary>Resizes the local grid and tells the server (SSH "window-change"), so programs reflow.</summary>
    public void Resize(int columns, int rows)
    {
        lock (Screen.SyncRoot)
        {
            if (columns == Screen.Columns && rows == Screen.Rows)
            {
                return;
            }
            Screen.Resize(columns, rows);
        }
        try
        {
            _stream?.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException)
        {
            // Not connected (yet): the size is sent when the shell opens
        }
    }

    public void Disconnect()
    {
        var stream = _stream;
        _stream = null;
        stream?.Dispose();
        try
        {
            _client?.Disconnect();
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        Disconnect();
        _client?.Dispose();
    }
}
