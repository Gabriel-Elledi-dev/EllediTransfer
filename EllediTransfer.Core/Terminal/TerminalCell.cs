namespace EllediTransfer.Terminal;

[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Hidden = 64,
    Strike = 128,
}

/// <summary>
/// Colours are -1 for the default colour, 0-255 for the xterm palette,
/// or <see cref="TrueColor"/> | 0xRRGGBB for 24-bit colour.
/// </summary>
public readonly record struct CellAttributes(int Foreground, int Background, CellFlags Flags)
{
    public const int DefaultColor = -1;
    public const int TrueColor = 0x1000000;

    public static readonly CellAttributes Default = new(DefaultColor, DefaultColor, CellFlags.None);

    public bool Has(CellFlags flag) => (Flags & flag) != 0;
}

public struct TerminalCell
{
    public int CodePoint;
    public CellAttributes Attributes;

    public static TerminalCell Blank(CellAttributes attributes) => new() { CodePoint = ' ', Attributes = attributes };
}

public sealed class TerminalLine(int columns, CellAttributes fill)
{
    public TerminalCell[] Cells { get; private set; } = CreateCells(columns, fill);

    /// <summary>The line continues on the next one because the text wrapped, not because of a newline.</summary>
    public bool Wrapped { get; set; }

    public void Resize(int columns)
    {
        if (columns == Cells.Length)
        {
            return;
        }
        var cells = CreateCells(columns, CellAttributes.Default);
        Array.Copy(Cells, cells, Math.Min(columns, Cells.Length));
        Cells = cells;
    }

    public void Clear(int from, int to, CellAttributes fill)
    {
        for (var i = Math.Max(0, from); i < Math.Min(to, Cells.Length); i++)
        {
            Cells[i] = TerminalCell.Blank(fill);
        }
    }

    public string GetText(int from = 0, int to = int.MaxValue)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = Math.Max(0, from); i < Math.Min(to, Cells.Length); i++)
        {
            builder.Append(char.ConvertFromUtf32(Cells[i].CodePoint));
        }
        return builder.ToString();
    }

    private static TerminalCell[] CreateCells(int columns, CellAttributes fill)
    {
        var cells = new TerminalCell[columns];
        Array.Fill(cells, TerminalCell.Blank(fill));
        return cells;
    }
}
