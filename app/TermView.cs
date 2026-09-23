// A terminal window onto one serial port: draws the port's Vt, sends what is
// typed to the port as an xterm would, keeps a scrollback, and copies and
// pastes.

using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace CorsacBench;

public sealed class TermView : Control
{
    public readonly Line Line;
    readonly VScrollBar _bar = new() { Dock = DockStyle.Right };
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 30 };
    long _drawn = -1;
    int _scroll;                        // lines back from the live screen
    long _pushedSeen;                   // the terminal's Pushed at the last paint
    Font _font = null!, _bold = null!;
    Size _cell;
    (int line, int col)? _selA, _selB;
    bool _selecting;
    public event Action<string>? Status;

    static readonly Color Fore = Color.FromArgb(204, 204, 204), Back = Color.FromArgb(12, 12, 12);
    static readonly Color[] Palette = BuildPalette();

    static Color[] BuildPalette()
    {
        var p = new Color[256];
        int[] basic =
        {
            0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
            0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2,
        };
        for (int i = 0; i < 16; i++) p[i] = Color.FromArgb(basic[i] >> 16, (basic[i] >> 8) & 255, basic[i] & 255);
        int[] steps = { 0, 95, 135, 175, 215, 255 };
        for (int i = 0; i < 216; i++) p[16 + i] = Color.FromArgb(steps[i / 36], steps[i / 6 % 6], steps[i % 6]);
        for (int i = 0; i < 24; i++) { int v = 8 + i * 10; p[232 + i] = Color.FromArgb(v, v, v); }
        return p;
    }

    public TermView(Line line)
    {
        Line = line;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        BackColor = Back;
        Cursor = Cursors.IBeam;
        Controls.Add(_bar);
        _bar.Scroll += (_, _) => { _scroll = _bar.Maximum - _bar.LargeChange + 1 - _bar.Value; Invalidate(); };
        _tick.Tick += (_, _) =>
        {
            bool off = Line.Term.Blinks && DateTime.UtcNow.Millisecond >= 500;
            if (Line.Term.Version != _drawn || off != _blinkOff) { _blinkOff = off; Invalidate(); }
        };
        _tick.Start();
        SetFont(Bench.Config.Font, Bench.Config.FontSize);
        ContextMenuStrip = BuildMenu();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            if (Line.SizeOwner == this) Line.SizeOwner = null;
        }
        base.Dispose(disposing);
    }

    public void SetFont(string family, float size)
    {
        var f = new Font(family, size, FontStyle.Regular, GraphicsUnit.Point);
        if (!f.Name.Equals(family, StringComparison.OrdinalIgnoreCase)) { f.Dispose(); f = new Font("Consolas", size); }
        _font = f;
        _bold = new Font(f, FontStyle.Bold);
        var m = TextRenderer.MeasureText("WWWWWWWWWW", _font, Size.Empty, TextFormatFlags.NoPadding);
        _cell = new Size((int)Math.Round(m.Width / 10.0), m.Height);
        lock (Line.Term) { Line.Term.CellW = _cell.Width; Line.Term.CellH = _cell.Height; }
        FitNow();
        Invalidate();
    }

    public Size CellSize => _cell;

    /// <summary>The client size that shows the terminal's whole screen.</summary>
    public Size WantedSize(int cols, int rows) => new(cols * _cell.Width + _bar.Width + 4, rows * _cell.Height + 4);

    /// <summary>The terminal is always as large as the view: resizing the window resizes the screen, as xterm does.</summary>
    public void FitNow()
    {
        if (Line.SizeOwner is TermView owner && owner != this && !owner.IsDisposed && owner.Visible) return;
        Line.SizeOwner ??= this;
        if (_cell.Width == 0 || ClientSize.Width < _cell.Width * 4 || ClientSize.Height < _cell.Height * 2) return;
        int cols = Math.Max(20, (ClientSize.Width - _bar.Width - 4) / _cell.Width);
        int rows = Math.Max(5, (ClientSize.Height - 4) / _cell.Height);
        lock (Line.Term) Line.Term.Resize(cols, rows);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FitNow();
        Invalidate();
    }

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Copy", null, (_, _) => Copy());
        m.Items.Add("Paste", null, (_, _) => Paste());
        m.Items.Add("Select all", null, (_, _) => { lock (Line.Term) { _selA = (0, 0); _selB = (Line.Term.TotalLines - 1, Line.Term.Cols); } Invalidate(); });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Clear scrollback", null, (_, _) => { lock (Line.Term) Line.Term.Scrollback.Clear(); _scroll = 0; Invalidate(); });
        m.Opening += (_, e) => { m.Items[0].Enabled = _selA != null; };
        return m;
    }

    // ---- drawing ----

    Color ColorOf(int c, bool fg, CellAttr a)
    {
        if (c < 0) return fg != _reverse ? Fore : Back;
        if (c >= 0x1000000) return Color.FromArgb((c >> 16) & 255, (c >> 8) & 255, c & 255);
        if (fg && (a & CellAttr.Bold) != 0 && c < 8) c += 8;
        return Palette[c & 255];
    }

    bool Selected(int line, int col)
    {
        if (_selA is not { } a || _selB is not { } b) return false;
        if ((b.line, b.col).CompareTo((a.line, a.col)) < 0) (a, b) = (b, a);
        return (line, col).CompareTo((a.line, a.col)) >= 0 && (line, col).CompareTo((b.line, b.col)) < 0;
    }

    bool _reverse, _blinkOff;
    static readonly TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.SingleLine;

    Color Screen0 => _reverse ? Fore : Back;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var vt = Line.Term;
        lock (vt)
        {
            _drawn = vt.Version;
            _reverse = vt.ReverseScreen;
            g.Clear(Screen0);
            int back = vt.AltScreen ? 0 : vt.Scrollback.Count;
            // SCROLLED BACK, THE TEXT HOLDS STILL: lines arriving below push
            // the view up by as many, so what is being read does not slide
            // away. At the bottom, the view follows the output.
            if (_scroll > 0) _scroll += (int)(vt.Pushed - _pushedSeen);
            _pushedSeen = vt.Pushed;
            _scroll = Math.Clamp(_scroll, 0, back);
            int first = back - _scroll;         // first line shown, in scrollback-then-screen order
            UpdateBar(back, vt.Rows);
            for (int y = 0; y < vt.Rows; y++)
            {
                int line = first + y;
                var row = vt.AltScreen ? vt.Row(y) : vt.LineCells(line);
                int py = 2 + y * _cell.Height;
                var size = vt.SizeOf(row);
                if (size == LineSize.Normal)
                {
                    DrawRow(g, row, line, 2, py, vt.Cols);
                    continue;
                }
                // A double line is drawn at normal size and then doubled, as the VT100 doubled its dots.
                int half = vt.Cols / 2;
                using var bmp = new Bitmap(half * _cell.Width, _cell.Height);
                using (var bg = Graphics.FromImage(bmp))
                {
                    bg.Clear(Screen0);
                    DrawRow(bg, row, line, 0, 0, half);
                }
                var state = g.Save();
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.SetClip(new Rectangle(2, py, half * 2 * _cell.Width, _cell.Height));
                var dst = size switch
                {
                    LineSize.DoubleTop => new Rectangle(2, py, bmp.Width * 2, _cell.Height * 2),
                    LineSize.DoubleBottom => new Rectangle(2, py - _cell.Height, bmp.Width * 2, _cell.Height * 2),
                    _ => new Rectangle(2, py, bmp.Width * 2, _cell.Height),
                };
                g.DrawImage(bmp, dst);
                g.Restore(state);
            }
            if (vt.CursorVisible && _scroll == 0)
            {
                int wide = vt.SizeOf(vt.Row(vt.CursorY)) == LineSize.Normal ? 1 : 2;
                var r = new Rectangle(2 + vt.CursorX * _cell.Width * wide, 2 + vt.CursorY * _cell.Height, _cell.Width * wide, _cell.Height);
                if (Focused)
                {
                    using var b = new SolidBrush(_reverse ? Color.FromArgb(150, 40, 40, 40) : Color.FromArgb(150, 220, 220, 220));
                    g.FillRectangle(b, r);
                }
                else
                {
                    using var p = new Pen(_reverse ? Color.FromArgb(60, 60, 60) : Color.FromArgb(200, 200, 200));
                    g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                }
            }
            string? note = !Line.IsOpen ? $"{Line.Name} is closed" : vt.KeyboardLocked ? "keyboard locked by the machine" : vt.Vt52 ? "VT52 mode" : null;
            if (note != null)
            {
                var size = TextRenderer.MeasureText(note, _bold);
                var at = new Point(ClientSize.Width - _bar.Width - size.Width - 12, 6);
                using var b = new SolidBrush(Color.FromArgb(160, 90, 0, 0));
                g.FillRectangle(b, at.X - 6, at.Y - 3, size.Width + 12, size.Height + 6);
                TextRenderer.DrawText(g, note, _bold, at, Color.White);
            }
        }
    }

    /// <summary>One line of cells, `cols` of them, with its top left at (x0, py).</summary>
    void DrawRow(Graphics g, Cell[] row, int line, int x0, int py, int cols)
    {
        var sb = new StringBuilder();
        int x = 0;
        while (x < row.Length && x < cols)
        {
            // A run of cells that draw alike.
            var c0 = row[x];
            bool sel0 = Selected(line, x);
            int end = x + 1;
            while (end < row.Length && end < cols && row[end].Fg == c0.Fg && row[end].Bg == c0.Bg && row[end].Attr == c0.Attr
                   && Selected(line, end) == sel0 && row[end].Ch < 0x2500 == c0.Ch < 0x2500)
                end++;
            var fg = ColorOf(c0.Fg, true, c0.Attr);
            var bg = ColorOf(c0.Bg, false, c0.Attr);
            if ((c0.Attr & CellAttr.Reverse) != 0) (fg, bg) = (bg, fg);
            if (sel0) (fg, bg) = (Color.Black, Color.FromArgb(160, 200, 255));
            if ((c0.Attr & CellAttr.Dim) != 0) fg = Color.FromArgb(fg.R * 2 / 3, fg.G * 2 / 3, fg.B * 2 / 3);
            if ((c0.Attr & CellAttr.Hidden) != 0 || (c0.Attr & CellAttr.Blink) != 0 && _blinkOff) fg = bg;
            int px = x0 + x * _cell.Width, width = (end - x) * _cell.Width;
            if (bg != Screen0)
                using (var b = new SolidBrush(bg)) g.FillRectangle(b, px, py, width, _cell.Height);
            sb.Clear();
            for (int i = x; i < end; i++) sb.Append(row[i].Ch == 0 ? ' ' : row[i].Ch);
            var text = sb.ToString();
            if (text.Trim().Length > 0 && fg != bg)
            {
                var font = (c0.Attr & CellAttr.Bold) != 0 ? _bold : _font;
                bool plain = c0.Ch < 0x2500;
                if (plain && text.All(ch => ch < 0x80))
                    TextRenderer.DrawText(g, text, font, new Point(px, py), fg, bg, Flags);
                else
                    // Wide or borrowed glyphs one per cell, so the grid holds.
                    for (int i = 0; i < text.Length; i++)
                        if (text[i] != ' ')
                            TextRenderer.DrawText(g, text[i].ToString(), font, new Rectangle(px + i * _cell.Width, py, _cell.Width, _cell.Height), fg, bg,
                                                  Flags | TextFormatFlags.HorizontalCenter);
            }
            if ((c0.Attr & CellAttr.Underline) != 0)
                using (var p = new Pen(fg)) g.DrawLine(p, px, py + _cell.Height - 1, px + width - 1, py + _cell.Height - 1);
            if ((c0.Attr & CellAttr.Strike) != 0)
                using (var p = new Pen(fg)) g.DrawLine(p, px, py + _cell.Height / 2, px + width - 1, py + _cell.Height / 2);
            x = end;
        }
    }

    void UpdateBar(int back, int rows)
    {
        _bar.Minimum = 0;
        _bar.LargeChange = rows;
        _bar.Maximum = back + rows - 1;
        _bar.Value = Math.Clamp(back - _scroll, 0, Math.Max(0, back));
        _bar.Enabled = back > 0;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll += e.Delta / 40;
        Invalidate();
    }

    // ---- selection ----

    (int line, int col) CellAt(Point p)
    {
        var vt = Line.Term;
        int back = vt.AltScreen ? 0 : vt.Scrollback.Count;
        int y = Math.Clamp((p.Y - 2) / Math.Max(1, _cell.Height), 0, vt.Rows - 1);
        int x = Math.Clamp((p.X - 2 + _cell.Width / 2) / Math.Max(1, _cell.Width), 0, vt.Cols);
        return (back - _scroll + y, x);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left)
        {
            lock (Line.Term) _selA = _selB = CellAt(e.Location);
            _selecting = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_selecting)
        {
            if (e.Y < 0) _scroll++;
            else if (e.Y > ClientSize.Height) _scroll--;
            lock (Line.Term) _selB = CellAt(e.Location);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _selecting = false;
        if (_selA == _selB) _selA = _selB = null;
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        lock (Line.Term)
        {
            var (line, col) = CellAt(e.Location);
            var text = Line.Term.LineText(line).PadRight(Line.Term.Cols);
            int a = Math.Min(col, text.Length - 1), b = a;
            while (a > 0 && !char.IsWhiteSpace(text[a - 1])) a--;
            while (b < text.Length && !char.IsWhiteSpace(text[b])) b++;
            _selA = (line, a); _selB = (line, b);
        }
        Invalidate();
    }

    public void Copy()
    {
        if (_selA is not { } a || _selB is not { } b) return;
        if ((b.line, b.col).CompareTo((a.line, a.col)) < 0) (a, b) = (b, a);
        var sb = new StringBuilder();
        lock (Line.Term)
            for (int line = a.line; line <= b.line; line++)
            {
                var t = Line.Term.LineText(line);
                int from = line == a.line ? Math.Min(a.col, t.Length) : 0;
                int to = line == b.line ? Math.Min(b.col, t.Length) : t.Length;
                sb.Append(t, from, Math.Max(0, to - from));
                if (line != b.line) sb.Append("\r\n");
            }
        if (sb.Length > 0) Clipboard.SetText(sb.ToString());
        _selA = _selB = null;
        Invalidate();
    }

    public void Paste()
    {
        if (!Clipboard.ContainsText()) return;
        var text = Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r');
        if (Line.Term.BracketedPaste) text = "\x1b[200~" + text + "\x1b[201~";
        Send(Encoding.Latin1.GetBytes(text));
    }

    // ---- keyboard ----

    void Send(byte[] data)
    {
        if (Line.Term.KeyboardLocked) { System.Media.SystemSounds.Beep.Play(); return; }
        try
        {
            Line.Send(data, "window");
            if (_scroll != 0) { _scroll = 0; Invalidate(); }
        }
        catch (Exception e) { Status?.Invoke(e.Message); }
    }

    void Send(string s) => Send(Encoding.Latin1.GetBytes(s));

    protected override bool IsInputKey(Keys keyData) => true;

    const int WM_SYSCHAR = 0x106;

    protected override void WndProc(ref Message m)
    {
        // Alt+key is meta: ESC then the key, as xterm does, for nano's M- commands.
        if (m.Msg == WM_SYSCHAR)
        {
            char c = (char)(long)m.WParam;
            if (c >= ' ' && c != (char)0x7F) { Send("\x1b" + c); return; }
        }
        base.WndProc(ref m);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool shift = e.Shift, ctrl = e.Control;
        if (ctrl && shift && e.KeyCode == Keys.C) { Copy(); e.Handled = e.SuppressKeyPress = true; return; }
        if (ctrl && shift && e.KeyCode == Keys.V) { Paste(); e.Handled = e.SuppressKeyPress = true; return; }
        if (shift && e.KeyCode == Keys.Insert) { Paste(); e.Handled = e.SuppressKeyPress = true; return; }
        if (ctrl && e.KeyCode == Keys.Insert) { Copy(); e.Handled = e.SuppressKeyPress = true; return; }
        if (shift && e.KeyCode == Keys.PageUp) { _scroll += Line.Term.Rows - 1; Invalidate(); e.Handled = e.SuppressKeyPress = true; return; }
        if (shift && e.KeyCode == Keys.PageDown) { _scroll -= Line.Term.Rows - 1; Invalidate(); e.Handled = e.SuppressKeyPress = true; return; }

        int mod = 1 + (shift ? 1 : 0) + (e.Alt ? 2 : 0) + (ctrl ? 4 : 0);
        string? seq = Keypad(e.KeyCode) ?? e.KeyCode switch
        {
            Keys.Up => CursorKey('A', mod),
            Keys.Down => CursorKey('B', mod),
            Keys.Right => CursorKey('C', mod),
            Keys.Left => CursorKey('D', mod),
            Keys.Home => CursorKey('H', mod),
            Keys.End => CursorKey('F', mod),
            Keys.Insert => Tilde(2, mod),
            Keys.Delete => Tilde(3, mod),
            Keys.PageUp => Tilde(5, mod),
            Keys.PageDown => Tilde(6, mod),
            // F1-F4 are the VT100's PF1-PF4.
            Keys.F1 => Line.Term.Vt52 ? "\x1bP" : mod == 1 ? "\x1bOP" : $"\x1b[1;{mod}P",
            Keys.F2 => Line.Term.Vt52 ? "\x1bQ" : mod == 1 ? "\x1bOQ" : $"\x1b[1;{mod}Q",
            Keys.F3 => Line.Term.Vt52 ? "\x1bR" : mod == 1 ? "\x1bOR" : $"\x1b[1;{mod}R",
            Keys.F4 => Line.Term.Vt52 ? "\x1bS" : mod == 1 ? "\x1bOS" : $"\x1b[1;{mod}S",
            Keys.F5 => Tilde(15, mod),
            Keys.F6 => Tilde(17, mod),
            Keys.F7 => Tilde(18, mod),
            Keys.F8 => Tilde(19, mod),
            Keys.F9 => Tilde(20, mod),
            Keys.F10 => Tilde(21, mod),
            Keys.F11 => Tilde(23, mod),
            Keys.F12 => Tilde(24, mod),
            Keys.Tab when shift => "\x1b[Z",
            Keys.Back => ctrl ? "\b" : e.Alt ? "\x1b\x7f" : "\x7f",   // Backspace is DEL, as on xterm and the Linux console
            Keys.Space when ctrl => "\0",
            Keys.D2 when ctrl && shift => "\0",
            Keys.D6 when ctrl && shift => "\x1e",
            Keys.OemMinus when ctrl && shift => "\x1f",
            _ => null,
        };
        if (seq != null)
        {
            Send(seq);
            e.Handled = e.SuppressKeyPress = true;
            return;
        }
        base.OnKeyDown(e);
    }

    string CursorKey(char c, int mod) =>
        Line.Term.Vt52 ? $"\x1b{c}" :
        mod != 1 ? $"\x1b[1;{mod}{c}" : Line.Term.AppCursorKeys ? $"\x1bO{c}" : $"\x1b[{c}";

    /// <summary>
    /// The numeric keypad in application mode (DECKPAM): ESC O p..y for the
    /// digits, as the VT100's keypad sent them; ESC ? instead of ESC O in VT52
    /// mode. The PC's - + * / stand for the VT100's - , and the xterm extras.
    /// In numeric mode the keys type their characters.
    /// </summary>
    string? Keypad(Keys k)
    {
        if (!Line.Term.AppKeypad) return null;
        char? c = k switch
        {
            >= Keys.NumPad0 and <= Keys.NumPad9 => (char)('p' + (k - Keys.NumPad0)),
            Keys.Decimal => 'n',
            Keys.Subtract => 'm',
            Keys.Add => 'l',
            Keys.Multiply => 'j',
            Keys.Divide => 'o',
            _ => null,
        };
        if (c == null) return null;
        return (Line.Term.Vt52 ? "\x1b?" : "\x1bO") + c;
    }

    const int WM_KEYDOWN = 0x100;

    /// <summary>The keypad's Enter is the main Enter with the extended-key bit; in application mode it is ESC O M.</summary>
    protected override bool ProcessKeyMessage(ref Message m)
    {
        if (m.Msg == WM_KEYDOWN && (Keys)(long)m.WParam == Keys.Return && ((long)m.LParam & (1 << 24)) != 0 && Line.Term.AppKeypad)
        {
            Send(Line.Term.Vt52 ? "\x1b?M" : "\x1bOM");
            _swallowEnter = true;
            return true;
        }
        return base.ProcessKeyMessage(ref m);
    }

    bool _swallowEnter;

    static string Tilde(int n, int mod) => mod == 1 ? $"\x1b[{n}~" : $"\x1b[{n};{mod}~";

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        char c = e.KeyChar;
        if (c == '\r')
        {
            if (_swallowEnter) _swallowEnter = false;
            else Send(Line.Term.NewLineMode ? "\r\n" : "\r");
        }
        else Send(Encoding.Latin1.GetBytes(c.ToString()) is { Length: 1 } one && c <= 0xFF ? one : Encoding.UTF8.GetBytes(c.ToString()));
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (Line.SizeOwner != this) { Line.SizeOwner = this; FitNow(); }
        Invalidate();
    }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}
