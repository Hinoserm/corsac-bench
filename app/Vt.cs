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
    Blink = 128,
}

/// <summary>A VT100 line's size: DECSWL, DECDWL, DECDHL top and bottom halves.</summary>
public enum LineSize : byte
{
    Normal,
    DoubleWidth,
    DoubleTop,
    DoubleBottom,
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
    EscapeHash,
    Vt52Row,
    Vt52Column,
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
    /// <summary>DECANM reset: the VT52 compatibility mode.</summary>
    public bool Vt52;
    /// <summary>DECSCNM: dark characters on a light screen.</summary>
    public bool ReverseScreen;
    /// <summary>LNM: a line feed also returns the carriage, and Return sends CR LF.</summary>
    public bool NewLineMode;
    /// <summary>KAM: the keyboard is locked.</summary>
    public bool KeyboardLocked;
    /// <summary>Set once anything blinks, so a view only runs its blink clock when it matters.</summary>
    public bool Blinks;
    /// <summary>Sent in answer to ENQ; the VT100's is empty until set up.</summary>
    public string Answerback = "";
    /// <summary>DECCOLM asked for 80 or 132 columns; the owner resizes its window to suit.</summary>
    public Action<int>? ColumnsWanted;
    bool _autoWrap = true, _originMode, _insertMode, _pendingWrap;
    int _top, _bottom;                  // scrolling region, inclusive
    Cell _pen = Cell.Blank;
    bool[] _tabs = Array.Empty<bool>();
    // The G0 and G1 character sets: 'B' US ASCII, 'A' UK, '0' DEC special graphics.
    char _g0 = 'B', _g1 = 'B';
    bool _shiftOut;
    int _vt52Row;
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<Cell[], object> _lineSizes = new();
    char _last = ' ';

    struct Saved { public int X, Y; public Cell Pen; public bool Origin, Wrap, Shift; public char G0, G1; }
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

    public LineSize SizeOf(Cell[] row) => _lineSizes.TryGetValue(row, out var s) ? (LineSize)s : LineSize.Normal;

    void SetSize(Cell[] row, LineSize size)
    {
        _lineSizes.Remove(row);
        if (size != LineSize.Normal) _lineSizes.Add(row, size);
    }

    /// <summary>The columns usable on the cursor's line: half of them on a double-width one.</summary>
    int LineCols => SizeOf(_screen[CursorY]) == LineSize.Normal ? Cols : Cols / 2;

    public void Resize(int cols, int rows)
    {
        if (cols < 2 || rows < 2 || (cols == Cols && rows == Rows)) return;
        bool alt = AltScreen;
        if (cols != Cols)
        {
            // A NEW WIDTH RE-WRAPS THE TEXT, as a modern terminal does: the
            // main screen and its scrollback are put back into the lines
            // they were written as and wrapped again at the new width, the
            // cursor staying on its character. The alternate screen belongs
            // to a full-screen program, which draws it again itself.
            Reflow(cols, rows, !alt);
        }
        else
        {
            _main = Refit(_main, cols, rows, !alt, true);
        }
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

    // ---- reflow -------------------------------------------------------------

    /// Rows that ran on into the next one because the text reached the right
    /// edge, rather than ending with a newline. Kept by the row, as its size
    /// is, so scrolling carries the mark with it.
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<Cell[], object> _wrapped = new();
    static readonly object WrapMark = new();

    bool Wrapped(Cell[] row) => _wrapped.TryGetValue(row, out _);

    void SetWrapped(Cell[] row, bool on)
    {
        _wrapped.Remove(row);
        if (on) _wrapped.Add(row, WrapMark);
    }

    /// How much of a row has anything in it: trailing spaces with nothing
    /// drawn behind them are not text.
    static int Used(Cell[] row)
    {
        int n = row.Length;
        while (n > 0)
        {
            var c = row[n - 1];
            if ((c.Ch != ' ' && c.Ch != 0) || c.Bg != -1 || (c.Attr & (CellAttr.Reverse | CellAttr.Underline | CellAttr.Strike)) != 0) break;
            n--;
        }
        return n;
    }

    /// The main screen and scrollback rewrapped at `cols`. `active` says the
    /// main screen is the one shown, so its cursor is the live one; under a
    /// full-screen program it is the one saved when that program took over.
    void Reflow(int cols, int rows, bool active)
    {
        int cx = active ? CursorX : _saved.X, cy = active ? CursorY : _saved.Y;
        var all = new List<Cell[]>(Scrollback.Count + _main.Length);
        all.AddRange(Scrollback);
        all.AddRange(_main);
        int cursorRow = Scrollback.Count + Math.Min(cy, _main.Length - 1);

        // Blank rows under the cursor are room, not text.
        int last = all.Count - 1;
        while (last > cursorRow && Used(all[last]) == 0 && !Wrapped(all[last - 1])) last--;

        // Back into the lines they were written as.
        var lines = new List<List<Cell>>();
        var line = new List<Cell>();
        int cursorLine = 0, cursorAt = 0;
        for (int i = 0; i <= last; i++)
        {
            var r = all[i];
            if (i == cursorRow) { cursorLine = lines.Count; cursorAt = line.Count + cx; }
            bool runsOn = Wrapped(r) && i < last;
            int take = runsOn ? r.Length : Used(r);
            for (int k = 0; k < take; k++) line.Add(r[k]);
            if (!runsOn) { lines.Add(line); line = new List<Cell>(); }
        }

        // And wrapped again.
        var rowsOut = new List<Cell[]>();
        int newRow = 0, newX = 0;
        for (int li = 0; li < lines.Count; li++)
        {
            var l = lines[li];
            int length = li == cursorLine ? Math.Max(l.Count, cursorAt + 1) : l.Count;
            int pieces = Math.Max(1, (length + cols - 1) / cols);
            int first = rowsOut.Count;
            for (int k = 0; k < pieces; k++)
            {
                var row = NewRow(cols);
                for (int x = 0; x < cols; x++)
                {
                    int at = k * cols + x;
                    if (at < l.Count) row[x] = l[at];
                }
                if (k < pieces - 1) SetWrapped(row, true);
                rowsOut.Add(row);
            }
            if (li == cursorLine) { newRow = first + cursorAt / cols; newX = cursorAt % cols; }
        }

        // The bottom of it is the screen, with the cursor on it.
        int start = Math.Max(0, rowsOut.Count - rows);
        if (newRow < start) start = newRow;
        if (newRow - start >= rows) start = newRow - rows + 1;
        Scrollback.Clear();
        Scrollback.AddRange(rowsOut.GetRange(0, start));
        if (Scrollback.Count > ScrollbackLimit) Scrollback.RemoveRange(0, Scrollback.Count - ScrollbackLimit);
        var screen = new Cell[rows][];
        for (int y = 0; y < rows; y++) screen[y] = start + y < rowsOut.Count ? rowsOut[start + y] : NewRow(cols);
        _main = screen;
        if (active) { CursorX = newX; CursorY = newRow - start; }
        else { _saved.X = newX; _saved.Y = newRow - start; }
    }

    /// <summary>The cursor's screen keeps the cursor's line by dropping lines from the top; the main screen's go to the scrollback.</summary>
    Cell[][] Refit(Cell[][] old, int cols, int rows, bool active, bool history)
    {
        var rowsOut = new List<Cell[]>();
        foreach (var r in old)
        {
            var n = NewRow(cols);
            Array.Copy(r, n, Math.Min(cols, r.Length));
            SetSize(n, SizeOf(r));
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
            if (c == 0x18) { _state = VtState.Ground; return; }
            // SUB cancels a sequence and shows the error character in its place.
            if (c == 0x1A) { _state = VtState.Ground; Print('▒'); return; }
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
                // 1 and 2 are the alternate character ROM, standard and special: the same sets here.
                char set = c == '0' || c == '2' ? '0' : c == 'A' ? 'A' : 'B';
                if (_inter == '(') _g0 = set;
                else if (_inter == ')') _g1 = set;
                _state = VtState.Ground;
                return;
            case VtState.EscapeHash:
                _state = VtState.Ground;
                switch (c)
                {
                    case '3': SetSize(_screen[CursorY], LineSize.DoubleTop); ClampToLine(); break;
                    case '4': SetSize(_screen[CursorY], LineSize.DoubleBottom); ClampToLine(); break;
                    case '5': SetSize(_screen[CursorY], LineSize.Normal); break;
                    case '6': SetSize(_screen[CursorY], LineSize.DoubleWidth); ClampToLine(); break;
                    case '8': AlignmentTest(); break;
                }
                return;
            case VtState.Vt52Row:
                _vt52Row = c - 32;
                _state = VtState.Vt52Column;
                return;
            case VtState.Vt52Column:
                _state = VtState.Ground;
                CursorY = Math.Clamp(_vt52Row, 0, Rows - 1);
                CursorX = Math.Clamp(c - 32, 0, LineCols - 1);
                _pendingWrap = false;
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
            case (char)0x05:
                if (Answerback.Length > 0) Reply?.Invoke(Encoding.Latin1.GetBytes(Answerback));
                break;
            case '\a': break;
            case '\b':
                if (_pendingWrap) _pendingWrap = false;
                else if (CursorX > 0) CursorX--;
                break;
            case '\t':
                _pendingWrap = false;
                do CursorX++; while (CursorX < LineCols - 1 && !_tabs[CursorX]);
                CursorX = Math.Min(CursorX, LineCols - 1);
                break;
            case '\n': case '\v': case '\f':
                if (NewLineMode) CursorX = 0;
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
        char set = _shiftOut ? _g1 : _g0;
        if (set == '0' && c >= '`' && c <= '~') c = LineDrawing(c);
        else if (set == 'A' && c == '#') c = '£';
        int cols = LineCols;
        if (_pendingWrap && _autoWrap)
        {
            // This row runs on into the next: one line, as far as reflow is concerned.
            SetWrapped(_screen[CursorY], true);
            CursorX = 0;
            LineFeed();
        }
        _pendingWrap = false;
        var row = _screen[CursorY];
        cols = LineCols;
        CursorX = Math.Min(CursorX, cols - 1);
        if (_insertMode)
            Array.Copy(row, CursorX, row, CursorX + 1, cols - CursorX - 1);
        var cell = _pen; cell.Ch = c;
        row[CursorX] = cell;
        _last = c;
        if (CursorX == cols - 1) _pendingWrap = true;
        else CursorX++;
    }

    void ClampToLine()
    {
        CursorX = Math.Min(CursorX, LineCols - 1);
        _pendingWrap = false;
    }

    /// <summary>DECALN: the screen filled with E, for lining up a monitor.</summary>
    void AlignmentTest()
    {
        _top = 0; _bottom = Rows - 1;
        for (int y = 0; y < Rows; y++)
        {
            _screen[y] = NewRow(Cols, new Cell { Ch = 'E', Fg = -1, Bg = -1 });
        }
        CursorX = CursorY = 0;
        _pendingWrap = false;
    }

    /// <summary>The VT52's escapes, which are not ANSI ones.</summary>
    void Vt52Escape(char c)
    {
        _state = VtState.Ground;
        _pendingWrap = false;
        switch (c)
        {
            case 'A': CursorY = Math.Max(0, CursorY - 1); break;
            case 'B': CursorY = Math.Min(Rows - 1, CursorY + 1); break;
            case 'C': CursorX = Math.Min(LineCols - 1, CursorX + 1); break;
            case 'D': CursorX = Math.Max(0, CursorX - 1); break;
            case 'F': _g0 = '0'; _shiftOut = false; break;
            case 'G': _g0 = 'B'; break;
            case 'H': CursorX = CursorY = 0; break;
            case 'I': ReverseIndex(); break;
            case 'J': EraseDisplay(0); break;
            case 'K': EraseLine(0); break;
            case 'Y': _state = VtState.Vt52Row; break;
            case 'Z': Reply?.Invoke(Encoding.ASCII.GetBytes("\x1b/Z")); break;
            case '=': AppKeypad = true; break;
            case '>': AppKeypad = false; break;
            case '<': Vt52 = false; break;
        }
    }

    void EscapeFinal(char c)
    {
        _state = VtState.Ground;
        if (Vt52) { Vt52Escape(c); return; }
        switch (c)
        {
            case '[': Enter(VtState.Csi); break;
            case ']': _osc.Clear(); _state = VtState.Osc; break;
            case 'P': case 'X': case '^': case '_': _state = VtState.Dcs; break;
            case '(': case ')': case '*': case '+': _inter = c; _state = VtState.EscapeCharset; break;
            case '#': _state = VtState.EscapeHash; break;
            case '%': case ' ': _inter = c; _state = VtState.EscapeCharset; break;
            case 'Z': Reply?.Invoke(Encoding.ASCII.GetBytes("\x1b[?1;2c")); break;   // DECID
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
        Vt52 = ReverseScreen = NewLineMode = KeyboardLocked = false;
        _g0 = _g1 = 'B'; _shiftOut = false;
        ResetTabs();
    }

    void SaveCursor()
    {
        var s = new Saved { X = CursorX, Y = CursorY, Pen = _pen, Origin = _originMode, Wrap = _autoWrap, G0 = _g0, G1 = _g1, Shift = _shiftOut };
        if (AltScreen) _savedAlt = s; else _saved = s;
    }

    void RestoreCursor()
    {
        var s = AltScreen ? _savedAlt : _saved;
        CursorX = Math.Min(s.X, Cols - 1); CursorY = Math.Min(s.Y, Rows - 1);
        _pen = s.Pen.Ch == 0 ? Cell.Blank : s.Pen;
        _originMode = s.Origin; _autoWrap = s.Wrap || s.Pen.Ch == 0;
        _g0 = s.G0 == 0 ? 'B' : s.G0; _g1 = s.G1 == 0 ? 'B' : s.G1; _shiftOut = s.Shift;
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
            case 'C': case 'a': CursorX = Math.Min(LineCols - 1, CursorX + P(0, 1)); _pendingWrap = false; break;
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
                foreach (var p in _params)
                    switch (p)
                    {
                        case 2: KeyboardLocked = final == 'h'; break;     // KAM
                        case 4: _insertMode = final == 'h'; break;        // IRM
                        case 20: NewLineMode = final == 'h'; break;       // LNM
                    }
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
                if (P0(0) == 0) Reply?.Invoke(Encoding.ASCII.GetBytes("\x1b[?1;2c"));    // a VT100 with the advanced video option
                break;
            case 'x':
                // DECREQTPARM: no parity, 8 bits, 9600 both ways, clock 16x, no flags. 0 asks for a report now and when settings change, 1 only when asked.
                if (P0(0) <= 1) Reply?.Invoke(Encoding.ASCII.GetBytes($"\x1b[{P0(0) + 2};1;1;112;112;1;0x"));
                break;
            case 'q': break;                  // DECLL: the keyboard LEDs; there are none to light
            case 'y': break;                  // DECTST: self-tests pass by not running
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
            case 2: if (!on) Vt52 = true; break;                  // DECANM reset: into VT52 mode; ESC < leaves it
            case 3:                                                // DECCOLM: 80 or 132 columns, the screen cleared
                ColumnsWanted?.Invoke(on ? 132 : 80);
                Resize(on ? 132 : 80, Rows);
                _top = 0; _bottom = Rows - 1;
                EraseDisplay(2);
                CursorX = CursorY = 0;
                break;
            case 4: case 8: case 9: break;                         // smooth scroll, auto-repeat, interlace: nothing to do
            case 5: ReverseScreen = on; break;                     // DECSCNM
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
                case 5: case 6: _pen.Attr |= CellAttr.Blink; Blinks = true; break;
                case 25: _pen.Attr &= ~CellAttr.Blink; break;
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
