using System.Globalization;
using System.Text;

namespace EllediTransfer.Terminal;

/// <summary>
/// Decodes the UTF-8 byte stream sent by the server and applies the xterm control sequences
/// (C0 controls, ESC, CSI, OSC; DCS/SOS/PM/APC strings are skipped) to a <see cref="TerminalScreen"/>.
/// </summary>
public sealed class VtParser(TerminalScreen screen)
{
    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        CsiIgnore,
        OscString,
        IgnoreString,
    }

    private readonly Decoder _utf8 = new UTF8Encoding(false).GetDecoder();
    private readonly List<int> _parameters = [];
    private readonly StringBuilder _intermediates = new();
    private readonly StringBuilder _oscText = new();
    private char[] _chars = new char[4096];
    private State _state;
    private char _privateMarker;
    private int _currentParameter = -1;
    private bool _stringEscape;
    private char _highSurrogate;

    /// <summary>Text the terminal must send back to the server (answers to DA, DSR, ENQ…).</summary>
    public event Action<string>? Reply;

    /// <summary>The program changed the window title (OSC 0 / OSC 2).</summary>
    public event Action<string>? TitleChanged;

    public event Action? Bell;

    /// <summary>Text sent back on ^E; PuTTY's default answerback is empty.</summary>
    public string Answerback { get; set; } = "";

    public void Feed(ReadOnlySpan<byte> data)
    {
        var needed = _utf8.GetCharCount(data, false);
        if (needed > _chars.Length)
        {
            _chars = new char[needed * 2];
        }
        var count = _utf8.GetChars(data, _chars, false);

        lock (screen.SyncRoot)
        {
            for (var i = 0; i < count; i++)
            {
                var c = _chars[i];
                if (char.IsHighSurrogate(c))
                {
                    _highSurrogate = c;
                    continue;
                }
                int codePoint = c;
                if (char.IsLowSurrogate(c))
                {
                    if (_highSurrogate == 0)
                    {
                        continue;
                    }
                    codePoint = char.ConvertToUtf32(_highSurrogate, c);
                }
                _highSurrogate = '\0';
                Process(codePoint);
            }
        }
    }

    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    private void Process(int c)
    {
        // CAN and SUB abort any sequence; ESC starts a new one (except inside strings, handled below)
        if (c is 0x18 or 0x1A)
        {
            _state = State.Ground;
            return;
        }

        switch (_state)
        {
            case State.Ground:
                if (c < 0x20 || c == 0x7F)
                {
                    Execute(c);
                }
                else
                {
                    _lastPrinted = c;
                    screen.Print(c);
                }
                break;

            case State.Escape:
                ProcessEscape(c);
                break;

            case State.EscapeIntermediate:
                if (c < 0x20)
                {
                    Execute(c);
                }
                else if (c is >= 0x20 and <= 0x2F)
                {
                    _intermediates.Append((char)c);
                }
                else
                {
                    DispatchEscape(c);
                    _state = State.Ground;
                }
                break;

            case State.Csi:
                ProcessCsi(c);
                break;

            case State.CsiIgnore:
                if (c < 0x20)
                {
                    Execute(c);
                }
                else if (c is >= 0x40 and <= 0x7E)
                {
                    _state = State.Ground;
                }
                break;

            case State.OscString:
                if (c == 0x07)
                {
                    DispatchOsc();
                    _state = State.Ground;
                }
                else if (_stringEscape)
                {
                    _stringEscape = false;
                    if (c == '\\')
                    {
                        DispatchOsc();
                        _state = State.Ground;
                    }
                    else
                    {
                        _state = State.Escape;
                        ProcessEscape(c);
                    }
                }
                else if (c == 0x1B)
                {
                    _stringEscape = true;
                }
                else if (_oscText.Length < 4096)
                {
                    _oscText.Append(char.ConvertFromUtf32(c));
                }
                break;

            case State.IgnoreString:
                if (c == 0x07 && !_stringEscape)
                {
                    _state = State.Ground;
                }
                else if (_stringEscape)
                {
                    _stringEscape = false;
                    _state = c == '\\' ? State.Ground : State.IgnoreString;
                }
                else if (c == 0x1B)
                {
                    _stringEscape = true;
                }
                break;
        }
    }

    private void Execute(int c)
    {
        switch (c)
        {
            case 0x05:
                if (Answerback.Length > 0)
                {
                    Reply?.Invoke(Answerback);
                }
                break;
            case 0x07:
                Bell?.Invoke();
                break;
            case 0x08:
                screen.Backspace();
                break;
            case 0x09:
                screen.Tab();
                break;
            case 0x0A or 0x0B or 0x0C:
                screen.LineFeed();
                break;
            case 0x0D:
                screen.CarriageReturn();
                break;
            case 0x0E:
                screen.ShiftCharset(1);
                break;
            case 0x0F:
                screen.ShiftCharset(0);
                break;
            case 0x1B:
                _state = State.Escape;
                _intermediates.Clear();
                break;
        }
    }

    private void ProcessEscape(int c)
    {
        _intermediates.Clear();
        switch (c)
        {
            case '[':
                _state = State.Csi;
                _parameters.Clear();
                _currentParameter = -1;
                _privateMarker = '\0';
                break;
            case ']':
                _state = State.OscString;
                _oscText.Clear();
                _stringEscape = false;
                break;
            case 'P' or 'X' or '^' or '_':
                _state = State.IgnoreString;
                _stringEscape = false;
                break;
            case >= 0x20 and <= 0x2F:
                _intermediates.Append((char)c);
                _state = State.EscapeIntermediate;
                break;
            case < 0x20:
                Execute(c);
                break;
            default:
                DispatchEscape(c);
                _state = State.Ground;
                break;
        }
    }

    private void DispatchEscape(int c)
    {
        var intermediate = _intermediates.Length > 0 ? _intermediates[0] : '\0';
        switch (intermediate)
        {
            case '(':
                screen.DesignateCharset(0, (char)c);
                return;
            case ')':
                screen.DesignateCharset(1, (char)c);
                return;
            case '#':
                if (c == '8')
                {
                    screen.FillWithE();
                }
                return;
            case not '\0':
                return; // e.g. ESC % G (select UTF-8): already the only encoding
        }

        switch (c)
        {
            case '7':
                screen.SaveCursor();
                break;
            case '8':
                screen.RestoreCursor();
                break;
            case 'D':
                screen.Index();
                break;
            case 'E':
                screen.NextLine();
                break;
            case 'M':
                screen.ReverseIndex();
                break;
            case 'H':
                screen.SetTabStop();
                break;
            case 'c':
                screen.Reset();
                break;
            case '=':
                screen.ApplicationKeypad = true;
                break;
            case '>':
                screen.ApplicationKeypad = false;
                break;
        }
    }

    private void ProcessCsi(int c)
    {
        if (c < 0x20)
        {
            Execute(c);
            return;
        }
        if (c is >= '0' and <= '9')
        {
            _currentParameter = Math.Min(99999, Math.Max(0, _currentParameter) * 10 + (c - '0'));
        }
        else if (c is ';' or ':')
        {
            _parameters.Add(_currentParameter);
            _currentParameter = -1;
        }
        else if (c is '?' or '>' or '<' or '=' && _parameters.Count == 0 && _currentParameter < 0 && _privateMarker == '\0')
        {
            _privateMarker = (char)c;
        }
        else if (c is >= 0x20 and <= 0x2F)
        {
            _intermediates.Append((char)c);
        }
        else if (c is >= 0x40 and <= 0x7E)
        {
            _parameters.Add(_currentParameter);
            DispatchCsi((char)c);
            _state = State.Ground;
        }
        else
        {
            _state = State.CsiIgnore;
        }
    }

    /// <summary>Parameter n, with <paramref name="fallback"/> for missing or zero values where 0 means "default".</summary>
    private int P(int index, int fallback = 1)
    {
        var value = index < _parameters.Count ? _parameters[index] : -1;
        return value <= 0 ? fallback : value;
    }

    /// <summary>Parameter n where 0 is a meaningful value.</summary>
    private int P0(int index) => index < _parameters.Count && _parameters[index] > 0 ? _parameters[index] : 0;

    private void DispatchCsi(char final)
    {
        var intermediate = _intermediates.Length > 0 ? _intermediates[0] : '\0';

        if (_privateMarker == '?')
        {
            if (final is 'h' or 'l')
            {
                foreach (var mode in _parameters)
                {
                    SetPrivateMode(mode, final == 'h');
                }
            }
            else if (final == 'J')
            {
                screen.EraseInDisplay(P0(0));
            }
            else if (final == 'K')
            {
                screen.EraseInLine(P0(0));
            }
            return;
        }

        if (_privateMarker == '>')
        {
            if (final == 'c')
            {
                Reply?.Invoke("\x1b[>0;10;1c"); // secondary DA: VT100-class terminal
            }
            return;
        }

        if (_privateMarker != '\0')
        {
            return;
        }

        if (intermediate == ' ' && final == 'q')
        {
            screen.CursorStyle = P0(0);
            screen.CursorBlink = P0(0) is 0 or 1 or 3 or 5;
            screen.Touch();
            return;
        }
        if (intermediate == '!' && final == 'p')
        {
            screen.SoftReset();
            return;
        }
        if (intermediate != '\0')
        {
            return;
        }

        switch (final)
        {
            case '@': screen.InsertChars(P(0)); break;
            case 'A': screen.MoveBy(0, -P(0)); break;
            case 'B': screen.MoveBy(0, P(0)); break;
            case 'C': screen.MoveBy(P(0), 0); break;
            case 'D': screen.MoveBy(-P(0), 0); break;
            case 'E': screen.MoveBy(0, P(0)); screen.CarriageReturn(); break;
            case 'F': screen.MoveBy(0, -P(0)); screen.CarriageReturn(); break;
            case 'G' or '`': screen.MoveColumn(P(0) - 1); break;
            case 'H' or 'f': screen.MoveTo(P(1) - 1, P(0) - 1); break;
            case 'I': screen.Tab(P(0)); break;
            case 'J': screen.EraseInDisplay(P0(0)); break;
            case 'K': screen.EraseInLine(P0(0)); break;
            case 'L': screen.InsertLines(P(0)); break;
            case 'M': screen.DeleteLines(P(0)); break;
            case 'P': screen.DeleteChars(P(0)); break;
            case 'S': screen.ScrollUp(P(0)); break;
            case 'T': screen.ScrollDown(P(0)); break;
            case 'X': screen.EraseChars(P(0)); break;
            case 'Z': screen.BackTab(P(0)); break;
            case 'a': screen.MoveBy(P(0), 0); break;
            case 'b': RepeatLast(P(0)); break;
            case 'c': Reply?.Invoke("\x1b[?6c"); break; // primary DA: VT102
            case 'd': screen.MoveRow(P(0) - 1); break;
            case 'e': screen.MoveBy(0, P(0)); break;
            case 'g': screen.ClearTabStop(P0(0)); break;
            case 'h' or 'l': SetAnsiModes(final == 'h'); break;
            case 'm': SelectGraphicRendition(); break;
            case 'n': DeviceStatusReport(); break;
            case 'r': screen.SetScrollRegion(P(0) - 1, P(1, screen.Rows) - 1); break;
            case 's': screen.SaveCursor(); break;
            case 'u': screen.RestoreCursor(); break;
            case 't' when P0(0) == 18: Reply?.Invoke($"\x1b[8;{screen.Rows};{screen.Columns}t"); break;
        }
    }

    private int _lastPrinted = ' ';

    /// <summary>REP: repeats the last printed character.</summary>
    private void RepeatLast(int count)
    {
        for (var i = 0; i < Math.Min(count, 65535); i++)
        {
            screen.Print(_lastPrinted);
        }
    }

    private void SetAnsiModes(bool on)
    {
        foreach (var mode in _parameters)
        {
            switch (mode)
            {
                case 4:
                    screen.InsertMode = on;
                    break;
                case 20:
                    screen.NewLineMode = on;
                    break;
            }
        }
    }

    private void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: screen.ApplicationCursorKeys = on; break;
            case 5: screen.ReverseVideo = on; break;
            case 6: screen.SetOriginMode(on); break;
            case 7: screen.AutoWrap = on; break;
            case 9: screen.MouseTracking = on ? MouseTracking.X10 : MouseTracking.None; break;
            case 12: screen.CursorBlink = on; break;
            case 25: screen.CursorVisible = on; break;
            case 47 or 1047: screen.SetAlternateScreen(on, clear: on); break;
            case 1048:
                if (on)
                {
                    screen.SaveCursor();
                }
                else
                {
                    screen.RestoreCursor();
                }
                break;
            case 1049:
                if (on)
                {
                    screen.SaveCursor();
                    screen.SetAlternateScreen(true, clear: true);
                }
                else
                {
                    screen.SetAlternateScreen(false, clear: false);
                    screen.RestoreCursor();
                }
                break;
            case 1000: screen.MouseTracking = on ? MouseTracking.Normal : MouseTracking.None; break;
            case 1002: screen.MouseTracking = on ? MouseTracking.ButtonEvent : MouseTracking.None; break;
            case 1003: screen.MouseTracking = on ? MouseTracking.AnyEvent : MouseTracking.None; break;
            case 1004: screen.FocusEvents = on; break;
            case 1006: screen.SgrMouse = on; break;
            case 2004: screen.BracketedPaste = on; break;
        }
        screen.Touch();
    }

    private void DeviceStatusReport()
    {
        switch (P0(0))
        {
            case 5:
                Reply?.Invoke("\x1b[0n");
                break;
            case 6:
                Reply?.Invoke($"\x1b[{screen.RelativeCursorY + 1};{screen.CursorX + 1}R");
                break;
        }
    }

    private void SelectGraphicRendition()
    {
        var a = screen.Attributes;
        for (var i = 0; i < _parameters.Count; i++)
        {
            var p = Math.Max(0, _parameters[i]);
            switch (p)
            {
                case 0: a = CellAttributes.Default; break;
                case 1: a = a with { Flags = a.Flags | CellFlags.Bold }; break;
                case 2: a = a with { Flags = a.Flags | CellFlags.Dim }; break;
                case 3: a = a with { Flags = a.Flags | CellFlags.Italic }; break;
                case 4 or 21: a = a with { Flags = a.Flags | CellFlags.Underline }; break;
                case 5 or 6: a = a with { Flags = a.Flags | CellFlags.Blink }; break;
                case 7: a = a with { Flags = a.Flags | CellFlags.Inverse }; break;
                case 8: a = a with { Flags = a.Flags | CellFlags.Hidden }; break;
                case 9: a = a with { Flags = a.Flags | CellFlags.Strike }; break;
                case 22: a = a with { Flags = a.Flags & ~(CellFlags.Bold | CellFlags.Dim) }; break;
                case 23: a = a with { Flags = a.Flags & ~CellFlags.Italic }; break;
                case 24: a = a with { Flags = a.Flags & ~CellFlags.Underline }; break;
                case 25: a = a with { Flags = a.Flags & ~CellFlags.Blink }; break;
                case 27: a = a with { Flags = a.Flags & ~CellFlags.Inverse }; break;
                case 28: a = a with { Flags = a.Flags & ~CellFlags.Hidden }; break;
                case 29: a = a with { Flags = a.Flags & ~CellFlags.Strike }; break;
                case >= 30 and <= 37: a = a with { Foreground = p - 30 }; break;
                case 38: a = a with { Foreground = ExtendedColor(ref i, a.Foreground) }; break;
                case 39: a = a with { Foreground = CellAttributes.DefaultColor }; break;
                case >= 40 and <= 47: a = a with { Background = p - 40 }; break;
                case 48: a = a with { Background = ExtendedColor(ref i, a.Background) }; break;
                case 49: a = a with { Background = CellAttributes.DefaultColor }; break;
                case >= 90 and <= 97: a = a with { Foreground = p - 90 + 8 }; break;
                case >= 100 and <= 107: a = a with { Background = p - 100 + 8 }; break;
            }
        }
        screen.Attributes = a;
    }

    /// <summary>38/48;5;n (256 colours) or 38/48;2;r;g;b (true colour).</summary>
    private int ExtendedColor(ref int i, int current)
    {
        int Next(ref int index) => ++index < _parameters.Count ? Math.Max(0, _parameters[index]) : 0;

        var kind = Next(ref i);
        if (kind == 5)
        {
            return Math.Clamp(Next(ref i), 0, 255);
        }
        if (kind == 2)
        {
            var r = Math.Clamp(Next(ref i), 0, 255);
            var g = Math.Clamp(Next(ref i), 0, 255);
            var b = Math.Clamp(Next(ref i), 0, 255);
            return CellAttributes.TrueColor | (r << 16) | (g << 8) | b;
        }
        return current;
    }

    private void DispatchOsc()
    {
        var text = _oscText.ToString();
        var separator = text.IndexOf(';');
        if (separator < 0 || !int.TryParse(text.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var command))
        {
            return;
        }
        if (command is 0 or 2)
        {
            TitleChanged?.Invoke(text[(separator + 1)..]);
        }
    }
}
