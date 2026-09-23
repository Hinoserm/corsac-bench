// The bench's terminal: a tab for each serial port, live whether an assistant
// or the person at the keyboard is driving it.

using System.Drawing;
using System.IO.Ports;
using System.Windows.Forms;

namespace CorsacBench;

public sealed class TerminalForm : Form
{
    readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden };
    readonly ToolStripComboBox _port = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 260 };
    readonly ToolStripButton _open = new("Open");
    readonly ToolStripDropDownButton _settings = new("Settings");
    readonly ToolStripDropDownButton _size = new("Size");
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly StatusStrip _status = new();
    readonly ToolStripStatusLabel _state = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _clients = new();
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };

    public TerminalForm()
    {
        Text = "CORSAC bench - terminal";
        Icon = TrayIcon.Make();
        StartPosition = FormStartPosition.Manual;
        _bar.Items.Add(new ToolStripLabel("Port"));
        _bar.Items.Add(_port);
        _bar.Items.Add(_open);
        _bar.Items.Add(_settings);
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.Add(new ToolStripButton("Reset machine", null, (_, _) => ResetMachine()) { ToolTipText = "Send ESC ESC ESC RESET: the CORSAC kernel resets the machine" });
        _bar.Items.Add(new ToolStripButton("Break", null, (_, _) => SendBreak()) { ToolTipText = "Hold the line in BREAK for 250 ms" });
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.Add(_size);
        _bar.Items.Add(new ToolStripButton("Screens", null, (_, _) => TrayIcon.ShowScreens()));
        _status.Items.Add(_state);
        _status.Items.Add(_clients);
        Controls.Add(_tabs);
        Controls.Add(_bar);
        Controls.Add(_status);

        _port.DropDown += (_, _) => FillPorts();
        _port.SelectedIndexChanged += (_, _) => { if (_port.SelectedItem is PortItem p) ShowLine(Bench.Line(p.Name)); };
        _open.Click += (_, _) => ToggleOpen();
        _settings.DropDownOpening += (_, _) => BuildSettings();
        _size.DropDownOpening += (_, _) => BuildSize();
        _tabs.SelectedIndexChanged += (_, _) => { UpdateStatus(); Current?.Focus(); };
        _tick.Tick += (_, _) => UpdateStatus();
        _tick.Start();
        Bench.LinesChanged += () => { if (IsHandleCreated) BeginInvoke(SyncTabs); };

        SyncTabs();
        ShowLine(Bench.Line(null));
        Shown += (_, _) => SizeFor(Bench.Config.Cols, Bench.Config.Rows);
        ResizeEnd += (_, _) => Remember();
    }

    sealed record PortItem(string Name, string Text)
    {
        public override string ToString() => Text;
    }

    TermView? Current => _tabs.SelectedTab?.Controls.OfType<TermView>().FirstOrDefault();

    void FillPorts()
    {
        var desc = Bench.PortDescriptions();
        var names = Bench.ComPorts().Concat(Bench.Lines.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        var cur = Current?.Line.Name;
        _port.Items.Clear();
        foreach (var n in names.OrderBy(n => n.Length).ThenBy(n => n))
        {
            Bench.Lines.TryGetValue(n.ToUpperInvariant(), out var l);
            var item = new PortItem(n.ToUpperInvariant(), $"{n}  {desc.GetValueOrDefault(n, "")}{(l?.IsOpen == true ? "  (open)" : "")}");
            _port.Items.Add(item);
            if (item.Name == cur) _port.SelectedItem = item;
        }
    }

    /// <summary>A tab for every port that has been opened, or asked about, this session.</summary>
    void SyncTabs()
    {
        List<Line> lines;
        lock (Bench.Lines) lines = Bench.Lines.Values.Where(l => l.IsOpen || l.End > 0 || l.Name == Bench.DefaultPort).ToList();
        foreach (var l in lines.OrderBy(l => l.Name.Length).ThenBy(l => l.Name))
            if (!_tabs.TabPages.ContainsKey(l.Name))
            {
                AddTab(l);
            }
        foreach (TabPage p in _tabs.TabPages)
            p.Text = p.Name + (Bench.Lines.TryGetValue(p.Name, out var l) && l.IsOpen ? "" : " (closed)");
        UpdateStatus();
    }

    void AddTab(Line l)
    {
        var view = new TermView(l) { Dock = DockStyle.Fill };
        view.Status += s => _state.Text = s;
        // DECCOLM: the machine asks for 80 or 132 columns and the window follows.
        l.Term.ColumnsWanted = cols => { if (IsHandleCreated) BeginInvoke(() => { if (Current?.Line == l) SizeFor(cols, l.Term.Rows); }); };
        var page = new TabPage(l.Name) { Name = l.Name };
        page.Controls.Add(view);
        _tabs.TabPages.Add(page);
    }

    void ShowLine(Line l)
    {
        SyncTabs();
        if (!_tabs.TabPages.ContainsKey(l.Name))
        {
            AddTab(l);
        }
        _tabs.SelectedTab = _tabs.TabPages[l.Name];
        UpdateStatus();
        Current?.Focus();
    }

    void ToggleOpen()
    {
        var l = Current?.Line;
        if (l == null) return;
        try
        {
            if (l.IsOpen) l.Close("window");
            else l.Open(null, "window");
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, $"{l.Name} did not open", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        SyncTabs();
    }

    void BuildSettings()
    {
        _settings.DropDownItems.Clear();
        var l = Current?.Line;
        if (l == null) return;
        ToolStripMenuItem Pick<T>(string title, IEnumerable<(string text, T value)> choices, T now, Action<LineSettings, T> set)
        {
            var m = new ToolStripMenuItem(title);
            foreach (var (text, value) in choices)
                m.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) =>
                {
                    var s = l.Settings.Clone();
                    set(s, value);
                    try { l.Configure(s, "window"); } catch (Exception e) { _state.Text = e.Message; }
                }) { Checked = Equals(value, now) });
            return m;
        }
        var st = l.Settings;
        _settings.DropDownItems.Add(Pick("Speed", new[] { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 }.Select(b => (b.ToString(), b)), st.Baud, (s, v) => s.Baud = v));
        _settings.DropDownItems.Add(Pick("Data bits", new[] { 8, 7, 6, 5 }.Select(b => (b.ToString(), b)), st.ByteSize, (s, v) => s.ByteSize = v));
        _settings.DropDownItems.Add(Pick("Parity", new[] { ("None", Parity.None), ("Even", Parity.Even), ("Odd", Parity.Odd), ("Mark", Parity.Mark), ("Space", Parity.Space) }, st.Parity, (s, v) => s.Parity = v));
        _settings.DropDownItems.Add(Pick("Stop bits", new[] { ("1", StopBits.One), ("1.5", StopBits.OnePointFive), ("2", StopBits.Two) }, st.StopBits, (s, v) => s.StopBits = v));
        _settings.DropDownItems.Add(Pick("Flow control", new[] { ("None", 0), ("RTS/CTS", 1), ("XON/XOFF", 2) }, st.RtsCts ? 1 : st.XonXoff ? 2 : 0, (s, v) => { s.RtsCts = v == 1; s.XonXoff = v == 2; }));
        _settings.DropDownItems.Add(new ToolStripSeparator());
        _settings.DropDownItems.Add(new ToolStripMenuItem("Default port for the assistants", null, (_, _) => { Bench.DefaultPort = l.Name; Bench.Save(); }) { Checked = Bench.DefaultPort == l.Name });
        _settings.DropDownItems.Add(new ToolStripMenuItem("Open the log", null, (_, _) => Open(l.LogPath)));
    }

    void BuildSize()
    {
        _size.DropDownItems.Clear();
        var now = Current?.Line.Term;
        foreach (var (c, r) in new[] { (80, 24), (80, 25), (80, 43), (80, 50), (100, 37), (132, 25), (132, 43), (132, 50) })
            _size.DropDownItems.Add(new ToolStripMenuItem($"{c} x {r}", null, (_, _) => SizeFor(c, r))
                { Checked = now != null && now.Cols == c && now.Rows == r });
        _size.DropDownItems.Add(new ToolStripMenuItem("(or drag the window to any size)") { Enabled = false });
        _size.DropDownItems.Add(new ToolStripSeparator());
        foreach (var pt in new[] { 8f, 9f, 10f, 11f, 12f, 14f, 16f })
            _size.DropDownItems.Add(new ToolStripMenuItem($"Text {pt} pt", null, (_, _) =>
            {
                int c = now?.Cols ?? Bench.Config.Cols, r = now?.Rows ?? Bench.Config.Rows;
                Bench.Config.FontSize = pt; Bench.Save();
                foreach (var t in AllViews()) t.SetFont(Bench.Config.Font, pt);
                SizeFor(c, r);
            }) { Checked = Bench.Config.FontSize == pt });
    }

    IEnumerable<TermView> AllViews() => _tabs.TabPages.Cast<TabPage>().SelectMany(p => p.Controls.OfType<TermView>());

    /// <summary>Sizes the window so the terminal comes out `cols` by `rows`.</summary>
    public void SizeFor(int cols, int rows)
    {
        var v = Current;
        if (v == null) return;
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        var want = v.WantedSize(cols, rows);
        var extra = Size - v.ClientSize;
        if (v.ClientSize.Width == 0) extra = Size - ClientSize + new Size(8, _bar.Height + _status.Height + 28);
        var area = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(area.Width, want.Width + extra.Width), Math.Min(area.Height, want.Height + extra.Height));
        if (!area.Contains(Bounds)) Location = new Point(Math.Max(area.X, Math.Min(Left, area.Right - Width)), Math.Max(area.Y, Math.Min(Top, area.Bottom - Height)));
        foreach (var t in AllViews()) t.FitNow();
        Remember();
    }

    /// <summary>The size the terminal has now is the one the next start opens at.</summary>
    void Remember()
    {
        var vt = Current?.Line.Term;
        if (vt == null || WindowState != FormWindowState.Normal) return;
        if (Bench.Config.Cols != vt.Cols || Bench.Config.Rows != vt.Rows)
        {
            Bench.Config.Cols = vt.Cols; Bench.Config.Rows = vt.Rows;
            Bench.Save();
        }
    }

    void UpdateStatus()
    {
        var l = Current?.Line;
        if (l != null)
        {
            _open.Text = l.IsOpen ? "Close" : "Open";
            _state.Text = $"{l.Describe()}   {l.End:N0} bytes received" +
                          (l.OpenedBy != "" ? $"   opened by {l.OpenedBy}" : "") +
                          (l.ConversationHolder != "" ? $"   running a command for {l.ConversationHolder}" : "") +
                          (l.Error != "" ? "   " + l.Error : "");
        }
        int n; string doing;
        lock (Bench.Clients)
        {
            n = Bench.Clients.Count;
            doing = string.Join(", ", Bench.Clients.Values.Where(c => c.Doing != "").Select(c => $"{c.Label}: {c.Doing}"));
        }
        _clients.Text = $"{n} session{(n == 1 ? "" : "s")}{(doing != "" ? "  " + doing : "")}";
    }

    void ResetMachine()
    {
        var l = Current?.Line;
        if (l == null) return;
        if (MessageBox.Show(this, $"Reset the machine on {l.Name} now?", "Reset", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try { l.Send(Bench.Magic, "window"); } catch (Exception e) { _state.Text = e.Message; }
    }

    void SendBreak()
    {
        var l = Current?.Line;
        if (l == null) return;
        try { l.Break(TimeSpan.FromMilliseconds(250)); } catch (Exception e) { _state.Text = e.Message; }
    }

    static void Open(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }
}
