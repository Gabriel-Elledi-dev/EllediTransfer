using System.Globalization;

namespace EllediTransfer;

public enum TransferMode
{
    Binary,
    Ascii,
    Automatic,
}

public enum OverwriteMode
{
    Overwrite,
    Resume,
    Append,
}

public enum TransferResumeSupportState
{
    Default,
    On,
    Off,
    Smart,
}

public sealed class TransferResumeSupport
{
    public TransferResumeSupportState State { get; set; } = TransferResumeSupportState.Default;

    /// <summary>Threshold in KB for <see cref="TransferResumeSupportState.Smart"/> (WinSCP default is 100 KB).</summary>
    public int Threshold { get; set; } = 100;

    internal bool UseTemporaryFile(long length) => State switch
    {
        TransferResumeSupportState.On => true,
        TransferResumeSupportState.Off => false,
        _ => length >= Threshold * 1024L,
    };
}

public sealed class FilePermissions
{
    public FilePermissions(int numeric)
    {
        if (numeric is < 0 or > 0xFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(numeric));
        }
        Numeric = numeric;
    }

    public int Numeric { get; }

    public string Octal => Convert.ToString(Numeric, 8).PadLeft(3, '0');

    public static FilePermissions Parse(string octal)
    {
        try
        {
            return new FilePermissions(Convert.ToInt32(octal, 8));
        }
        catch (FormatException)
        {
            throw new FormatException($"Invalid permissions \"{octal}\", expected octal value such as 644.");
        }
    }

    public override string ToString() => Octal;
}

public sealed class TransferOptions
{
    /// <summary>Default ASCII mask of WinSCP, as found in the shipped WinSCP.exe.</summary>
    public const string DefaultAsciiFileMask =
        "*.*html; *.htm; *.txt; *.php; *.php3; *.cgi; *.c; *.cpp; *.h; *.pas; *.bas; *.tex; *.pl; *.js; " +
        ".htaccess; *.xtml; *.css; *.cfg; *.ini; *.sh; *.xml";

    public TransferMode TransferMode { get; set; } = TransferMode.Binary;
    public string? FileMask { get; set; }
    public bool PreserveTimestamp { get; set; } = true;
    public FilePermissions? FilePermissions { get; set; }
    public TransferResumeSupport ResumeSupport { get; set; } = new();
    /// <summary>Speed limit in KB/s, 0 means unlimited.</summary>
    public int SpeedLimit { get; set; }
    public OverwriteMode OverwriteMode { get; set; } = OverwriteMode.Overwrite;
    public string AsciiFileMask { get; set; } = DefaultAsciiFileMask;

    internal bool IsAscii(string fileName) => TransferMode switch
    {
        TransferMode.Ascii => true,
        TransferMode.Automatic => EllediTransfer.FileMask.Parse(AsciiFileMask).Matches(fileName, false),
        _ => false,
    };

    public override string ToString()
    {
        var parts = new List<string> { "-transfer=" + TransferMode.ToString().ToLowerInvariant() };
        if (!string.IsNullOrEmpty(FileMask))
        {
            parts.Add($"-filemask=\"{FileMask}\"");
        }
        parts.Add(PreserveTimestamp ? "-preservetime" : "-nopreservetime");
        if (FilePermissions != null)
        {
            parts.Add("-permissions=" + FilePermissions.Octal);
        }
        if (SpeedLimit > 0)
        {
            parts.Add("-speed=" + SpeedLimit.ToString(CultureInfo.InvariantCulture));
        }
        if (OverwriteMode != OverwriteMode.Overwrite)
        {
            parts.Add("-" + OverwriteMode.ToString().ToLowerInvariant());
        }
        return string.Join(" ", parts);
    }
}
