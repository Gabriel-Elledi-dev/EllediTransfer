using System.Text;

namespace EllediTransfer.Terminal;

public enum MouseTracking
{
    None,
    X10,
    Normal,
    ButtonEvent,
    AnyEvent,
}

/// <summary>
/// The character grid of an xterm-compatible terminal: main and alternate screen, scrollback,
/// cursor, scroll region, tab stops and modes. Callers must hold <see cref="SyncRoot"/>.
/// </summary>
public sealed class TerminalScreen
{
    private readonly List<TerminalLine> _scrollback = [];
    private TerminalLine[] _main;
    private TerminalLine[] _alternate;
    private bool[] _tabStops;
    private bool _wrapPending;
    private int _top;
    private int _bottom;
    private SavedCursor _savedMain;
    private SavedCursor _savedAlternate;

    // G0/G1 character sets: 'B' = ASCII, '0' = DEC special graphics (line drawing)
    private readonly char[] _charsets = ['B', 'B'];
    private int _activeCharset;

    public TerminalScreen(int columns, int rows, int scrollbackLimit = 5000)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        ScrollbackLimit = scrollbackLimit;
        _main = CreateLines(Rows, Columns);
        _alternate = CreateLines(Rows, Columns);
        _tabStops = CreateTabStops(Columns);
        _bottom = Rows - 1;
        _savedMain = _savedAlternate = new SavedCursor(0, 0, CellAttributes.Default, false, 'B', 'B', 0);
    }

    public object SyncRoot { get; } = new();

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int ScrollbackLimit { get; }
    public int ScrollbackCount => _scrollback.Count;

    /// <summary>Incremented on every change, so a view knows when to repaint.</summary>
    public int Version { get; private set; }

    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public CellAttributes Attributes { get; set; } = CellAttributes.Default;

    public bool AlternateScreen { get; private set; }
    public bool AutoWrap { get; set; } = true;
    public bool OriginMode { get; private set; }
    public bool InsertMode { get; set; }
    public bool NewLineMode { get; set; }
    public bool CursorVisible { get; set; } = true;
    public bool CursorBlink { get; set; } = true;
    public int CursorStyle { get; set; }
    public bool ReverseVideo { get; set; }
    public bool ApplicationCursorKeys { get; set; }
    public bool ApplicationKeypad { get; set; }
    public bool BracketedPaste { get; set; }
    public bool FocusEvents { get; set; }
    public MouseTracking MouseTracking { get; set; }
    public bool SgrMouse { get; set; }

    private TerminalLine[] Lines => AlternateScreen ? _alternate : _main;

    /// <summary>Line by absolute index: scrollback first (0 = oldest), then the visible screen.</summary>
    public TerminalLine GetLine(int index) =>
        index < _scrollback.Count ? _scrollback[index] : Lines[index - _scrollback.Count];

    public int TotalLines => _scrollback.Count + Rows;

    public void Touch() => Version++;

    #region Printing and cursor movement

    public void Print(int codePoint)
    {
        if (_charsets[_activeCharset] == '0' && codePoint is >= 0x5F and <= 0x7E)
        {
            codePoint = DecSpecialGraphics[codePoint - 0x5F];
        }

        if (_wrapPending && AutoWrap)
        {
            Lines[CursorY].Wrapped = true;
            CursorX = 0;
            Index();
        }
        _wrapPending = false;

        var line = Lines[CursorY];
        if (InsertMode)
        {
            Array.Copy(line.Cells, CursorX, line.Cells, CursorX + 1, Columns - CursorX - 1);
        }
        line.Cells[CursorX] = new TerminalCell { CodePoint = codePoint, Attributes = Attributes };

        if (CursorX == Columns - 1)
        {
            _wrapPending = AutoWrap;
        }
        else
        {
            CursorX++;
        }
        Version++;
    }

    public void CarriageReturn()
    {
        CursorX = 0;
        _wrapPending = false;
        Version++;
    }

    public void LineFeed()
    {
        Index();
        if (NewLineMode)
        {
            CursorX = 0;
        }
    }

    /// <summary>Moves down one line, scrolling the region when at its bottom (IND).</summary>
    public void Index()
    {
        _wrapPending = false;
        if (CursorY == _bottom)
        {
            ScrollUp(1);
        }
        else if (CursorY < Rows - 1)
        {
            CursorY++;
        }
        Version++;
    }

    /// <summary>Moves up one line, scrolling the region down when at its top (RI).</summary>
    public void ReverseIndex()
    {
        _wrapPending = false;
        if (CursorY == _top)
        {
            ScrollDown(1);
        }
        else if (CursorY > 0)
        {
            CursorY--;
        }
        Version++;
    }

    public void NextLine()
    {
        CarriageReturn();
        Index();
    }

    public void Backspace()
    {
        _wrapPending = false;
        if (CursorX > 0)
        {
            CursorX--;
        }
        Version++;
    }

    public void Tab(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            var x = CursorX + 1;
            while (x < Columns - 1 && !_tabStops[x])
            {
                x++;
            }
            CursorX = Math.Min(x, Columns - 1);
        }
        _wrapPending = false;
        Version++;
    }

    public void BackTab(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            var x = CursorX - 1;
            while (x > 0 && !_tabStops[x])
            {
                x--;
            }
            CursorX = Math.Max(0, x);
        }
        _wrapPending = false;
        Version++;
    }

    public void SetTabStop() => _tabStops[CursorX] = true;

    public void ClearTabStop(int mode)
    {
        if (mode == 0)
        {
            _tabStops[CursorX] = false;
        }
        else if (mode == 3)
        {
            Array.Clear(_tabStops);
        }
    }

    /// <summary>Absolute positioning (CUP); coordinates are 0-based and relative to the region in origin mode.</summary>
    public void MoveTo(int x, int y)
    {
        var minY = OriginMode ? _top : 0;
        var maxY = OriginMode ? _bottom : Rows - 1;
        CursorX = Math.Clamp(x, 0, Columns - 1);
        CursorY = Math.Clamp(y + minY, minY, maxY);
        _wrapPending = false;
        Version++;
    }

    public void MoveColumn(int x)
    {
        CursorX = Math.Clamp(x, 0, Columns - 1);
        _wrapPending = false;
        Version++;
    }

    public void MoveRow(int y) => MoveTo(CursorX, y);

    /// <summary>Relative movement (CUU/CUD/CUF/CUB); vertical moves stop at the scroll region.</summary>
    public void MoveBy(int dx, int dy)
    {
        var minY = CursorY >= _top ? _top : 0;
        var maxY = CursorY <= _bottom ? _bottom : Rows - 1;
        CursorX = Math.Clamp(CursorX + dx, 0, Columns - 1);
        CursorY = Math.Clamp(CursorY + dy, minY, maxY);
        _wrapPending = false;
        Version++;
    }

    public int RelativeCursorY => OriginMode ? CursorY - _top : CursorY;

    #endregion

    #region Scrolling and editing

    public void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top, 0, Rows - 1);
        bottom = Math.Clamp(bottom, 0, Rows - 1);
        if (bottom <= top)
        {
            return;
        }
        _top = top;
        _bottom = bottom;
        MoveTo(0, 0);
    }

    public void ScrollUp(int count)
    {
        var lines = Lines;
        count = Math.Min(count, _bottom - _top + 1);
        for (var i = 0; i < count; i++)
        {
            var removed = lines[_top];
            if (!AlternateScreen && _top == 0)
            {
                AddToScrollback(removed);
            }
            Array.Copy(lines, _top + 1, lines, _top, _bottom - _top);
            lines[_bottom] = new TerminalLine(Columns, EraseAttributes);
        }
        Version++;
    }

    public void ScrollDown(int count)
    {
        var lines = Lines;
        count = Math.Min(count, _bottom - _top + 1);
        for (var i = 0; i < count; i++)
        {
            Array.Copy(lines, _top, lines, _top + 1, _bottom - _top);
            lines[_top] = new TerminalLine(Columns, EraseAttributes);
        }
        Version++;
    }

    public void InsertLines(int count)
    {
        if (CursorY < _top || CursorY > _bottom)
        {
            return;
        }
        var savedTop = _top;
        _top = CursorY;
        ScrollDown(count);
        _top = savedTop;
        CursorX = 0;
        _wrapPending = false;
    }

    public void DeleteLines(int count)
    {
        if (CursorY < _top || CursorY > _bottom)
        {
            return;
        }
        var lines = Lines;
        count = Math.Min(count, _bottom - CursorY + 1);
        for (var i = 0; i < count; i++)
        {
            Array.Copy(lines, CursorY + 1, lines, CursorY, _bottom - CursorY);
            lines[_bottom] = new TerminalLine(Columns, EraseAttributes);
        }
        CursorX = 0;
        _wrapPending = false;
        Version++;
    }

    public void InsertChars(int count)
    {
        var cells = Lines[CursorY].Cells;
        count = Math.Min(count, Columns - CursorX);
        Array.Copy(cells, CursorX, cells, CursorX + count, Columns - CursorX - count);
        Lines[CursorY].Clear(CursorX, CursorX + count, EraseAttributes);
        _wrapPending = false;
        Version++;
    }

    public void DeleteChars(int count)
    {
        var cells = Lines[CursorY].Cells;
        count = Math.Min(count, Columns - CursorX);
        Array.Copy(cells, CursorX + count, cells, CursorX, Columns - CursorX - count);
        Lines[CursorY].Clear(Columns - count, Columns, EraseAttributes);
        _wrapPending = false;
        Version++;
    }

    public void EraseChars(int count)
    {
        Lines[CursorY].Clear(CursorX, CursorX + count, EraseAttributes);
        _wrapPending = false;
        Version++;
    }

    /// <summary>ED: 0 = cursor to end, 1 = start to cursor, 2 = whole screen, 3 = scrollback.</summary>
    public void EraseInDisplay(int mode)
    {
        var lines = Lines;
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (var y = CursorY + 1; y < Rows; y++)
                {
                    lines[y] = new TerminalLine(Columns, EraseAttributes);
                }
                break;
            case 1:
                EraseInLine(1);
                for (var y = 0; y < CursorY; y++)
                {
                    lines[y] = new TerminalLine(Columns, EraseAttributes);
                }
                break;
            case 2:
                for (var y = 0; y < Rows; y++)
                {
                    lines[y] = new TerminalLine(Columns, EraseAttributes);
                }
                break;
            case 3:
                _scrollback.Clear();
                break;
        }
        Version++;
    }

    /// <summary>EL: 0 = cursor to end, 1 = start to cursor, 2 = whole line.</summary>
    public void EraseInLine(int mode)
    {
        var line = Lines[CursorY];
        switch (mode)
        {
            case 0:
                line.Clear(CursorX, Columns, EraseAttributes);
                line.Wrapped = false;
                break;
            case 1:
                line.Clear(0, CursorX + 1, EraseAttributes);
                break;
            case 2:
                line.Clear(0, Columns, EraseAttributes);
                line.Wrapped = false;
                break;
        }
        _wrapPending = false;
        Version++;
    }

    /// <summary>DECALN: fills the screen with 'E' (alignment test).</summary>
    public void FillWithE()
    {
        foreach (var line in Lines)
        {
            for (var x = 0; x < Columns; x++)
            {
                line.Cells[x] = new TerminalCell { CodePoint = 'E', Attributes = CellAttributes.Default };
            }
        }
        MoveTo(0, 0);
    }

    public void ClearScrollback()
    {
        _scrollback.Clear();
        Version++;
    }

    // Erased cells take the current background (background colour erase, as PuTTY and xterm do)
    private CellAttributes EraseAttributes => CellAttributes.Default with { Background = Attributes.Background };

    private void AddToScrollback(TerminalLine line)
    {
        _scrollback.Add(line);
        if (_scrollback.Count > ScrollbackLimit + 256)
        {
            _scrollback.RemoveRange(0, _scrollback.Count - ScrollbackLimit);
        }
    }

    #endregion

    #region Modes, charsets, saved state

    public void SetOriginMode(bool on)
    {
        OriginMode = on;
        MoveTo(0, 0);
    }

    public void DesignateCharset(int slot, char charset) => _charsets[slot] = charset;

    public void ShiftCharset(int slot) => _activeCharset = slot;

    public void SaveCursor()
    {
        var saved = new SavedCursor(CursorX, CursorY, Attributes, OriginMode, _charsets[0], _charsets[1], _activeCharset);
        if (AlternateScreen)
        {
            _savedAlternate = saved;
        }
        else
        {
            _savedMain = saved;
        }
    }

    public void RestoreCursor()
    {
        var saved = AlternateScreen ? _savedAlternate : _savedMain;
        CursorX = Math.Clamp(saved.X, 0, Columns - 1);
        CursorY = Math.Clamp(saved.Y, 0, Rows - 1);
        Attributes = saved.Attributes;
        OriginMode = saved.OriginMode;
        _charsets[0] = saved.G0;
        _charsets[1] = saved.G1;
        _activeCharset = saved.Active;
        _wrapPending = false;
        Version++;
    }

    /// <summary>Switches to or from the alternate screen used by full-screen programs (vim, htop, mc).</summary>
    public void SetAlternateScreen(bool on, bool clear)
    {
        if (on == AlternateScreen)
        {
            return;
        }
        AlternateScreen = on;
        if (on && clear)
        {
            _alternate = CreateLines(Rows, Columns);
        }
        _wrapPending = false;
        Version++;
    }

    /// <summary>RIS: full reset, keeping the scrollback.</summary>
    public void Reset()
    {
        _main = CreateLines(Rows, Columns);
        _alternate = CreateLines(Rows, Columns);
        _tabStops = CreateTabStops(Columns);
        AlternateScreen = false;
        Attributes = CellAttributes.Default;
        _top = 0;
        _bottom = Rows - 1;
        CursorX = CursorY = 0;
        _wrapPending = false;
        AutoWrap = true;
        OriginMode = InsertMode = NewLineMode = false;
        CursorVisible = CursorBlink = true;
        CursorStyle = 0;
        ReverseVideo = ApplicationCursorKeys = ApplicationKeypad = BracketedPaste = FocusEvents = SgrMouse = false;
        MouseTracking = MouseTracking.None;
        _charsets[0] = _charsets[1] = 'B';
        _activeCharset = 0;
        _savedMain = _savedAlternate = new SavedCursor(0, 0, CellAttributes.Default, false, 'B', 'B', 0);
        Version++;
    }

    /// <summary>DECSTR: soft reset of modes, keeping the screen content.</summary>
    public void SoftReset()
    {
        CursorVisible = true;
        InsertMode = OriginMode = false;
        AutoWrap = true;
        ApplicationCursorKeys = ApplicationKeypad = false;
        Attributes = CellAttributes.Default;
        _top = 0;
        _bottom = Rows - 1;
        _charsets[0] = _charsets[1] = 'B';
        _activeCharset = 0;
        _wrapPending = false;
        Version++;
    }

    #endregion

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        // Keep the cursor on screen: when the window gets shorter, the top lines go to the scrollback
        var main = _main.ToList();
        var shift = Math.Max(0, CursorY - (rows - 1));
        if (!AlternateScreen)
        {
            for (var i = 0; i < shift; i++)
            {
                AddToScrollback(main[0]);
                main.RemoveAt(0);
            }
        }
        _main = Fit(main, rows, columns);
        _alternate = Fit(_alternate.ToList(), rows, columns);
        foreach (var line in _scrollback)
        {
            line.Resize(columns);
        }

        if (!AlternateScreen)
        {
            CursorY -= shift;
        }
        Columns = columns;
        Rows = rows;
        CursorX = Math.Clamp(CursorX, 0, columns - 1);
        CursorY = Math.Clamp(CursorY, 0, rows - 1);
        _top = 0;
        _bottom = rows - 1;
        _tabStops = CreateTabStops(columns);
        _wrapPending = false;
        Version++;
    }

    /// <summary>Text between two absolute positions (inclusive start, exclusive end column), soft-wrapped lines joined.</summary>
    public string GetText(int startLine, int startColumn, int endLine, int endColumn)
    {
        var builder = new StringBuilder();
        for (var index = startLine; index <= endLine && index < TotalLines; index++)
        {
            var line = GetLine(index);
            var from = index == startLine ? startColumn : 0;
            var to = index == endLine ? endColumn : Columns;
            builder.Append(line.GetText(from, to).TrimEnd());
            if (index != endLine && !line.Wrapped)
            {
                builder.Append(Environment.NewLine);
            }
        }
        return builder.ToString();
    }

    private static TerminalLine[] Fit(List<TerminalLine> lines, int rows, int columns)
    {
        while (lines.Count > rows)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        while (lines.Count < rows)
        {
            lines.Add(new TerminalLine(columns, CellAttributes.Default));
        }
        foreach (var line in lines)
        {
            line.Resize(columns);
        }
        return lines.ToArray();
    }

    private static TerminalLine[] CreateLines(int rows, int columns)
    {
        var lines = new TerminalLine[rows];
        for (var i = 0; i < rows; i++)
        {
            lines[i] = new TerminalLine(columns, CellAttributes.Default);
        }
        return lines;
    }

    private static bool[] CreateTabStops(int columns)
    {
        var stops = new bool[columns];
        for (var x = 8; x < columns; x += 8)
        {
            stops[x] = true;
        }
        return stops;
    }

    private readonly record struct SavedCursor(int X, int Y, CellAttributes Attributes, bool OriginMode, char G0, char G1, int Active);

    // DEC special graphics for 0x5F..0x7E (box drawing used by mc, htop, dialog, ncurses)
    private static readonly int[] DecSpecialGraphics =
    [
        0x00A0, 0x25C6, 0x2592, 0x2409, 0x240C, 0x240D, 0x240A, 0x00B0, 0x00B1, 0x2424, 0x240B, 0x2518, 0x2510, 0x250C, 0x2514, 0x253C,
        0x23BA, 0x23BB, 0x2500, 0x23BC, 0x23BD, 0x251C, 0x2524, 0x2534, 0x252C, 0x2502, 0x2264, 0x2265, 0x03C0, 0x2260, 0x00A3, 0x00B7,
    ];
}
