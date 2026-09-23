// The terminal: an xterm-compatible screen model fed with the bytes a serial
// port receives. It keeps emulating whether or not a window shows it, so the
// window opened later shows the screen as it is now.

using System.Text;

namespace CorsacBench;

[Flags]
public enum CellAttr : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Reverse = 16,
    Hidden = 32,
    Strike = 64,
}

public struct Cell
{
    public char Ch;
    /// <summary>-1 is the default colour, 0-255 the xterm palette, 0x1000000|rgb a direct colour.</summary>
    public int Fg, Bg;
    public CellAttr Attr;

    public static readonly Cell Blank = new() { Ch = ' ', Fg = -1, Bg = -1 };
}

enum VtState
{
    Ground,
    Escape,
    EscapeCharset,
    Csi,
    Osc,
    OscEscape,
    Dcs,
    DcsEscape,
}

public sealed class Vt
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public const int ScrollbackLimit = 10000;

    /// <summary>Everything that scrolled off the top of the main screen, oldest first.</summary>
    public readonly List<Cell[]> Scrollback = new();
    Cell[][] _main, _alt, _screen;
    public bool AltScreen => _screen == _alt;

    public int CursorX, CursorY;
    public bool CursorVisible = true;
    public bool AppCursorKeys, AppKeypad, BracketedPaste;
    bool _autoWrap = true, _originMode, _insertMode, _pendingWrap;
    int _top, _bottom;                  // scrolling region, inclusive
    Cell _pen = Cell.Blank;
    bool[] _tabs = Array.Empty<bool>();
    bool _g0Lines, _g1Lines, _shiftOut;
    char _last = ' ';

    struct Saved { public int X, Y; public Cell Pen; public bool Origin, Wrap, G0, G1, Shift; }
    Saved _saved, _savedAlt;

    VtState _state;
    readonly List<int> _params = new();
    int _param = -1;
    char _prefix, _inter;
    readonly StringBuilder _osc = new();

    // UTF-8 decoding across reads; a byte that does not fit is taken as Latin-1.
    int _utfNeed, _utfCode;
    readonly List<byte> _utfBytes = new();

    /// <summary>Bytes the terminal answers with (cursor position reports); the owner sends them to the port.</summary>
    public Action<byte[]>? Reply;
    /// <summary>Counts every change, so a view knows when to repaint.</summary>
    public long Version;
    public string Title = "";

    public Vt(int cols = 80, int rows = 25)
    {
        Cols = cols; Rows = rows;
        _main = NewScreen(cols, rows);
        _alt = NewScreen(cols, rows);
        _screen = _main;
        _bottom = rows - 1;
        ResetTabs();
    }

    static Cell[][] NewScreen(int cols, int rows)
    {
        var s = new Cell[rows][];
        for (int i = 0; i < rows; i++) s[i] = NewRow(cols);
        return s;
    }

    static Cell[] NewRow(int cols, Cell fill = default)
    {
        if (fill.Ch == 0) fill = Cell.Blank;
        var r = new Cell[cols];
        Array.Fill(r, fill);
        return r;
    }

    void ResetTabs()
    {
        _tabs = new bool[Cols];
        for (int i = 8; i < Cols; i += 8) _tabs[i] = true;
    }

    public Cell[] Row(int y) => _screen[y];

    public void Resize(int cols, int rows)
    {
        if (cols < 2 || rows < 2 || (cols == Cols && rows == Rows)) return;
        bool alt = AltScreen;
        _main = Refit(_main, cols, rows, !alt, true);
        _alt = Refit(_alt, cols, rows, alt, false);
        _screen = alt ? _alt : _main;
        Cols = cols; Rows = rows;
        _top = 0; _bottom = rows - 1;
        CursorX = Math.Min(CursorX, cols - 1);
        CursorY = Math.Min(CursorY, rows - 1);
        _pendingWrap = false;
        ResetTabs();
        Version++;
    }

    /// <summary>The cursor's screen keeps the cursor's line by dropping lines from the top; the main screen's go to the scrollback.</summary>
    Cell[][] Refit(Cell[][] old, int cols, int rows, bool active, bool history)
    {
        var rowsOut = new List<Cell[]>();
        foreach (var r in old)
        {
            var n = NewRow(cols);
            Array.Copy(r, n, Math.Min(cols, r.Length));
            rowsOut.Add(n);
        }
        while (rowsOut.Count > rows)
        {
            if (active && CursorY > 0)
            {
                if (history) PushScrollback(rowsOut[0]);
                rowsOut.RemoveAt(0);
                CursorY--;
            }
            else
                rowsOut.RemoveAt(rowsOut.Count - 1);
        }
        while (rowsOut.Count < rows) rowsOut.Add(NewRow(cols));
        return rowsOut.ToArray();
    }

    void PushScrollback(Cell[] row)
    {
        Scrollback.Add(row);
        if (Scrollback.Count > ScrollbackLimit) Scrollback.RemoveRange(0, Scrollback.Count - ScrollbackLimit);
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) FeedByte(b);
        Version++;
    }

    void FeedByte(byte b)
    {
        if (_utfNeed > 0)
        {
            if ((b & 0xC0) == 0x80)
            {
                _utfCode = (_utfCode << 6) | (b & 0x3F);
                _utfBytes.Add(b);
                if (--_utfNeed == 0)
                {
                    _utfBytes.Clear();
                    if (_utfCode > 0xFFFF) _utfCode = 0xFFFD;
                    Put((char)_utfCode);
                }
                return;
            }
            // Not UTF-8 after all: what was held is Latin-1.
            var held = _utfBytes.ToArray();
            _utfBytes.Clear(); _utfNeed = 0;
            foreach (var h in held) Put((char)h);
        }
        if (b >= 0xC2 && b <= 0xF4 && _state == VtState.Ground)
        {
            _utfNeed = b >= 0xF0 ? 3 : b >= 0xE0 ? 2 : 1;
            _utfCode = b & (b >= 0xF0 ? 0x07 : b >= 0xE0 ? 0x0F : 0x1F);
            _utfBytes.Add(b);
            return;
        }
        Put((char)b);
    }

    void Put(char c)
    {
        // Controls act in every state except inside strings.
        if (c < 0x20 && _state != VtState.Osc && _state != VtState.Dcs)
        {
            if (c == 0x1B) { Enter(VtState.Escape); return; }
            if (c == 0x18 || c == 0x1A) { _state = VtState.Ground; return; }
            Control(c);
            return;
        }
        switch (_state)
        {
            case VtState.Ground:
                if (c == 0x7F) return;
                Print(c);
                return;
            case VtState.Escape:
                EscapeFinal(c);
                return;
            case VtState.EscapeCharset:
                if (_inter == '(') _g0Lines = c == '0';
                else if (_inter == ')') _g1Lines = c == '0';
                _state = VtState.Ground;
                return;
            case VtState.Csi:
                CsiByte(c);
                return;
            case VtState.Osc:
                if (c == 0x07) { OscDone(); return; }
                if (c == 0x1B) { _state = VtState.OscEscape; return; }
                if (_osc.Length < 4096) _osc.Append(c);
                return;
            case VtState.OscEscape:
                if (c == '\\') OscDone();
                else _state = VtState.Ground;
                return;
            case VtState.Dcs:
                if (c == 0x1B) _state = VtState.DcsEscape;
                return;
            case VtState.DcsEscape:
                _state = c == '\\' ? VtState.Ground : VtState.Dcs;
                return;
        }
    }

    void Enter(VtState s)
    {
        _state = s;
        _params.Clear(); _param = -1; _prefix = '\0'; _inter = '\0';
    }

    void Control(char c)
    {
        switch (c)
        {
            case '\a': break;
            case '\b':
                if (_pendingWrap) _pendingWrap = false;
                else if (CursorX > 0) CursorX--;
                break;
            case '\t':
                _pendingWrap = false;
                do CursorX++; while (CursorX < Cols - 1 && !_tabs[CursorX]);
                CursorX = Math.Min(CursorX, Cols - 1);
                break;
            case '\n': case '\v': case '\f':
                LineFeed();
                break;
            case '\r':
                CursorX = 0; _pendingWrap = false;
                break;
            case (char)0x0E: _shiftOut = true; break;
            case (char)0x0F: _shiftOut = false; break;
        }
    }

    void LineFeed()
    {
        _pendingWrap = false;
        if (CursorY == _bottom) ScrollUp(_top, _bottom, 1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    void ReverseIndex()
    {
        _pendingWrap = false;
        if (CursorY == _top) ScrollDown(_top, _bottom, 1);
        else if (CursorY > 0) CursorY--;
    }

    Cell BlankCell() => new() { Ch = ' ', Fg = -1, Bg = _pen.Bg };

    void ScrollUp(int top, int bottom, int n)
    {
        n = Math.Min(n, bottom - top + 1);
        for (int i = 0; i < n; i++)
        {
            var gone = _screen[top];
            if (top == 0 && _screen == _main) PushScrollback(gone);
            for (int y = top; y < bottom; y++) _screen[y] = _screen[y + 1];
            _screen[bottom] = NewRow(Cols, BlankCell());
        }
    }

    void ScrollDown(int top, int bottom, int n)
    {
        n = Math.Min(n, bottom - top + 1);
        for (int i = 0; i < n; i++)
        {
            for (int y = bottom; y > top; y--) _screen[y] = _screen[y - 1];
            _screen[top] = NewRow(Cols, BlankCell());
        }
    }

    static char LineDrawing(char c) => c switch
    {
        '`' => '◆', 'a' => '▒', 'f' => '°', 'g' => '±', 'j' => '┘', 'k' => '┐', 'l' => '┌', 'm' => '└',
        'n' => '┼', 'o' => '⎺', 'p' => '⎻', 'q' => '─', 'r' => '⎼', 's' => '⎽', 't' => '├', 'u' => '┤',
        'v' => '┴', 'w' => '┬', 'x' => '│', 'y' => '≤', 'z' => '≥', '{' => 'π', '|' => '≠', '}' => '£', '~' => '·',
        _ => c,
    };

    void Print(char c)
    {
        if ((_shiftOut ? _g1Lines : _g0Lines) && c >= '`' && c <= '~') c = LineDrawing(c);
        if (_pendingWrap && _autoWrap)
        {
            CursorX = 0;
            LineFeed();
        }
        _pendingWrap = false;
        var row = _screen[CursorY];
        if (_insertMode)
            Array.Copy(row, CursorX, row, CursorX + 1, Cols - CursorX - 1);
        var cell = _pen; cell.Ch = c;
        row[CursorX] = cell;
        _last = c;
        if (CursorX == Cols - 1) _pendingWrap = true;
        else CursorX++;
    }

    void EscapeFinal(char c)
    {
        _state = VtState.Ground;
        switch (c)
        {
            case '[': Enter(VtState.Csi); break;
            case ']': _osc.Clear(); _state = VtState.Osc; break;
            case 'P': case 'X': case '^': case '_': _state = VtState.Dcs; break;
            case '(': case ')': case '*': case '+': _inter = c; _state = VtState.EscapeCharset; break;
            case '#': case '%': case ' ': _inter = c; _state = VtState.EscapeCharset; break;
            case 'D': LineFeed(); break;
            case 'E': CursorX = 0; LineFeed(); break;
            case 'M': ReverseIndex(); break;
            case 'H': if (CursorX < Cols) _tabs[CursorX] = true; break;
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case '=': AppKeypad = true; break;
            case '>': AppKeypad = false; break;
            case 'c': FullReset(); break;
        }
    }

    void FullReset()
    {
        _main = NewScreen(Cols, Rows);
        _alt = NewScreen(Cols, Rows);
        _screen = _main;
        _top = 0; _bottom = Rows - 1;
        CursorX = CursorY = 0;
        _pen = Cell.Blank;
        _autoWrap = true; _originMode = _insertMode = _pendingWrap = false;
        CursorVisible = true;
        AppCursorKeys = AppKeypad = BracketedPaste = false;
        _g0Lines = _g1Lines = _shiftOut = false;
        ResetTabs();
    }

    void SaveCursor()
    {
        var s = new Saved { X = CursorX, Y = CursorY, Pen = _pen, Origin = _originMode, Wrap = _autoWrap, G0 = _g0Lines, G1 = _g1Lines, Shift = _shiftOut };
        if (AltScreen) _savedAlt = s; else _saved = s;
    }

    void RestoreCursor()
    {
        var s = AltScreen ? _savedAlt : _saved;
        CursorX = Math.Min(s.X, Cols - 1); CursorY = Math.Min(s.Y, Rows - 1);
        _pen = s.Pen.Ch == 0 ? Cell.Blank : s.Pen;
        _originMode = s.Origin; _autoWrap = s.Wrap || s.Pen.Ch == 0;
        _g0Lines = s.G0; _g1Lines = s.G1; _shiftOut = s.Shift;
        _pendingWrap = false;
    }

    void CsiByte(char c)
    {
        if (c >= '0' && c <= '9')
        {
            _param = (_param < 0 ? 0 : _param) * 10 + (c - '0');
            if (_param > 99999) _param = 99999;
            return;
        }
        if (c == ';' || c == ':')
        {
            _params.Add(_param); _param = -1;
            return;
        }
        if (c >= '<' && c <= '?') { _prefix = c; return; }
        if (c >= ' ' && c <= '/') { _inter = c; return; }
        _params.Add(_param);
        _state = VtState.Ground;
        if (c >= '@' && c <= '~') Csi(c);
    }

    int P(int i, int dflt) => i < _params.Count && _params[i] > 0 ? _params[i] : dflt;
    int P0(int i) => i < _params.Count && _params[i] > 0 ? _params[i] : 0;

    void MoveTo(int x, int y)
    {
        int top = _originMode ? _top : 0, bottom = _originMode ? _bottom : Rows - 1;
        CursorX = Math.Clamp(x, 0, Cols - 1);
        CursorY = Math.Clamp(y + top, top, bottom);
        _pendingWrap = false;
    }

    void Csi(char final)
    {
        if (_prefix == '?' && (final == 'h' || final == 'l'))
        {
            foreach (var p in _params) DecMode(p, final == 'h');
            return;
        }
        if (_prefix == '>' || _prefix == '=' || _inter != '\0')
        {
            if (_inter == '!' && final == 'p') FullReset();       // DECSTR, near enough
            return;
        }
        var row = _screen[CursorY];
        switch (final)
        {
            case 'A': CursorY = Math.Max(CursorY < _top ? 0 : _top, CursorY - P(0, 1)); _pendingWrap = false; break;
            case 'B': case 'e': CursorY = Math.Min(CursorY > _bottom ? Rows - 1 : _bottom, CursorY + P(0, 1)); _pendingWrap = false; break;
            case 'C': case 'a': CursorX = Math.Min(Cols - 1, CursorX + P(0, 1)); _pendingWrap = false; break;
            case 'D': CursorX = Math.Max(0, CursorX - P(0, 1)); _pendingWrap = false; break;
            case 'E': CursorX = 0; CursorY = Math.Min(_bottom, CursorY + P(0, 1)); _pendingWrap = false; break;
            case 'F': CursorX = 0; CursorY = Math.Max(_top, CursorY - P(0, 1)); _pendingWrap = false; break;
            case 'G': case '`': CursorX = Math.Clamp(P(0, 1) - 1, 0, Cols - 1); _pendingWrap = false; break;
            case 'd': MoveTo(CursorX, P(0, 1) - 1); break;
            case 'H': case 'f': MoveTo(P(1, 1) - 1, P(0, 1) - 1); break;
            case 'I': for (int i = P(0, 1); i > 0; i--) Control('\t'); break;
            case 'Z':
                for (int i = P(0, 1); i > 0; i--)
                    do CursorX = Math.Max(0, CursorX - 1); while (CursorX > 0 && !_tabs[CursorX]);
                break;
            case 'J': EraseDisplay(P0(0)); break;
            case 'K': EraseLine(P0(0)); break;
            case 'L':
                if (CursorY >= _top && CursorY <= _bottom) ScrollDown(CursorY, _bottom, P(0, 1));
                CursorX = 0; _pendingWrap = false;
                break;
            case 'M':
                if (CursorY >= _top && CursorY <= _bottom) ScrollUp(CursorY, _bottom, P(0, 1));
                CursorX = 0; _pendingWrap = false;
                break;
            case '@':
            {
                int n = Math.Min(P(0, 1), Cols - CursorX);
                Array.Copy(row, CursorX, row, CursorX + n, Cols - CursorX - n);
                for (int i = 0; i < n; i++) row[CursorX + i] = BlankCell();
                _pendingWrap = false;
                break;
            }
            case 'P':
            {
                int n = Math.Min(P(0, 1), Cols - CursorX);
                Array.Copy(row, CursorX + n, row, CursorX, Cols - CursorX - n);
                for (int i = Cols - n; i < Cols; i++) row[i] = BlankCell();
                _pendingWrap = false;
                break;
            }
            case 'X':
            {
                int n = Math.Min(P(0, 1), Cols - CursorX);
                for (int i = 0; i < n; i++) row[CursorX + i] = BlankCell();
                _pendingWrap = false;
                break;
            }
            case 'S': ScrollUp(_top, _bottom, P(0, 1)); break;
            case 'T': ScrollDown(_top, _bottom, P(0, 1)); break;
            case 'b': for (int i = Math.Min(P(0, 1), 65535); i > 0; i--) Print(_last); break;
            case 'g':
                if (P0(0) == 0) { if (CursorX < Cols) _tabs[CursorX] = false; }
                else if (P0(0) == 3) Array.Fill(_tabs, false);
                break;
            case 'h': case 'l':
                foreach (var p in _params) if (p == 4) _insertMode = final == 'h';
                break;
            case 'm': Sgr(); break;
            case 'n':
                if (P0(0) == 5) Reply?.Invoke(Encoding.ASCII.GetBytes("\x1b[0n"));
                else if (P0(0) == 6)
                {
                    int y = CursorY - (_originMode ? _top : 0);
                    Reply?.Invoke(Encoding.ASCII.GetBytes($"\x1b[{y + 1};{CursorX + 1}R"));
                }
                break;
            case 'c':
                if (P0(0) == 0) Reply?.Invoke(Encoding.ASCII.GetBytes("\x1b[?1;2c"));
                break;
            case 'r':
            {
                int top = P(0, 1) - 1, bottom = P(1, Rows) - 1;
                if (top < bottom && bottom < Rows) { _top = top; _bottom = bottom; }
                else { _top = 0; _bottom = Rows - 1; }
                MoveTo(0, 0);
                break;
            }
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 't': break;
        }
    }

    void DecMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: AppCursorKeys = on; break;
            case 6: _originMode = on; MoveTo(0, 0); break;
            case 7: _autoWrap = on; break;
            case 25: CursorVisible = on; break;
            case 2004: BracketedPaste = on; break;
            case 47: case 1047:
                SwitchScreen(on, false);
                break;
            case 1048:
                if (on) SaveCursor(); else RestoreCursor();
                break;
            case 1049:
                if (on) { SaveCursor(); SwitchScreen(true, true); }
                else { SwitchScreen(false, false); RestoreCursor(); }
                break;
        }
    }

    void SwitchScreen(bool alt, bool clear)
    {
        if (alt && !AltScreen)
        {
            if (clear) _alt = NewScreen(Cols, Rows);
            _screen = _alt;
        }
        else if (!alt && AltScreen)
            _screen = _main;
        _pendingWrap = false;
    }

    void EraseDisplay(int how)
    {
        switch (how)
        {
            case 0:
                EraseLine(0);
                for (int y = CursorY + 1; y < Rows; y++) _screen[y] = NewRow(Cols, BlankCell());
                break;
            case 1:
                EraseLine(1);
                for (int y = 0; y < CursorY; y++) _screen[y] = NewRow(Cols, BlankCell());
                break;
            case 2:
            case 3:
                if (how == 3 && _screen == _main) Scrollback.Clear();
                for (int y = 0; y < Rows; y++) _screen[y] = NewRow(Cols, BlankCell());
                break;
        }
        _pendingWrap = false;
    }

    void EraseLine(int how)
    {
        var row = _screen[CursorY];
        int from = how == 0 ? CursorX : 0, to = how == 1 ? CursorX + 1 : Cols;
        for (int x = from; x < Math.Min(to, Cols); x++) row[x] = BlankCell();
        _pendingWrap = false;
    }

    void Sgr()
    {
        if (_params.Count == 0) { _pen = Cell.Blank; return; }
        for (int i = 0; i < _params.Count; i++)
        {
            int p = Math.Max(0, _params[i]);
            switch (p)
            {
                case 0: _pen = Cell.Blank; break;
                case 1: _pen.Attr |= CellAttr.Bold; break;
                case 2: _pen.Attr |= CellAttr.Dim; break;
                case 3: _pen.Attr |= CellAttr.Italic; break;
                case 4: _pen.Attr |= CellAttr.Underline; break;
                case 7: _pen.Attr |= CellAttr.Reverse; break;
                case 8: _pen.Attr |= CellAttr.Hidden; break;
                case 9: _pen.Attr |= CellAttr.Strike; break;
                case 21: case 22: _pen.Attr &= ~(CellAttr.Bold | CellAttr.Dim); break;
                case 23: _pen.Attr &= ~CellAttr.Italic; break;
                case 24: _pen.Attr &= ~CellAttr.Underline; break;
                case 27: _pen.Attr &= ~CellAttr.Reverse; break;
                case 28: _pen.Attr &= ~CellAttr.Hidden; break;
                case 29: _pen.Attr &= ~CellAttr.Strike; break;
                case >= 30 and <= 37: _pen.Fg = p - 30; break;
                case 39: _pen.Fg = -1; break;
                case >= 40 and <= 47: _pen.Bg = p - 40; break;
                case 49: _pen.Bg = -1; break;
                case >= 90 and <= 97: _pen.Fg = p - 90 + 8; break;
                case >= 100 and <= 107: _pen.Bg = p - 100 + 8; break;
                case 38: case 48:
                {
                    int colour = -1;
                    if (i + 2 < _params.Count && _params[i + 1] == 5) { colour = Math.Clamp(_params[i + 2], 0, 255); i += 2; }
                    else if (i + 4 < _params.Count && _params[i + 1] == 2)
                    {
                        colour = 0x1000000 | (Math.Clamp(_params[i + 2], 0, 255) << 16) | (Math.Clamp(_params[i + 3], 0, 255) << 8) | Math.Clamp(_params[i + 4], 0, 255);
                        i += 4;
                    }
                    if (p == 38) _pen.Fg = colour; else _pen.Bg = colour;
                    break;
                }
            }
        }
    }

    void OscDone()
    {
        _state = VtState.Ground;
        var s = _osc.ToString();
        int semi = s.IndexOf(';');
        if (semi > 0 && (s[..semi] == "0" || s[..semi] == "2")) Title = s[(semi + 1)..];
    }

    /// <summary>The text of a line, screen rows counted after the scrollback.</summary>
    public string LineText(int line)
    {
        var r = line < Scrollback.Count ? Scrollback[line] : _screen[line - Scrollback.Count];
        var sb = new StringBuilder(r.Length);
        foreach (var c in r) sb.Append(c.Ch == 0 ? ' ' : c.Ch);
        return sb.ToString().TrimEnd();
    }

    public int TotalLines => Scrollback.Count + Rows;

    /// <summary>A line by its index in scrollback-then-screen order; null when the alternate screen hides the scrollback.</summary>
    public Cell[] LineCells(int line) => line < Scrollback.Count ? Scrollback[line] : _screen[line - Scrollback.Count];
}
