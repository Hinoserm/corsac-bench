// The bench's terminals: as many windows as wanted, each with a tab for each
// of its ports, live whether an assistant or the person at the keyboard is
// driving them. The windows, their ports, sizes and places are remembered and
// opened again at the next start. The first window also takes any port that
// is opened and not shown anywhere else, so nothing is ever open unseen.

using System.Drawing;
using System.IO.Ports;
using System.Windows.Forms;

namespace CorsacBench;

/// One terminal window: which ports it shows and where it was.
public sealed class TerminalWindowConfig
{
    public List<string> Ports { get; set; } = new();
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public int W { get; set; }
    public int H { get; set; }
    public bool Open { get; set; } = true;
}

public sealed class TerminalForm : Form
{
    public static readonly List<TerminalForm> Open = new();

    public readonly TerminalWindowConfig W;
    readonly ToolStrip _bar = new ClickThroughStrip { GripStyle = ToolStripGripStyle.Hidden };
    readonly ToolStripComboBox _port = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 260 };
    readonly ToolStripButton _open = new("Open");
    readonly ToolStripDropDownButton _settings = new("Settings");
    readonly ToolStripDropDownButton _record = new("Record");
    readonly ToolStripDropDownButton _size = new("Size");
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly StatusStrip _status = new ClickThroughStatusStrip();
    readonly ToolStripStatusLabel _rec = new() { ForeColor = Color.Firebrick };
    readonly ToolStripStatusLabel _state = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _clients = new();
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };
    bool _quitting;

    // ---- the windows -----------------------------------------------------------

    /// The windows already up brought forward, or those open at the last exit, or one new one.
    public static void ShowAll()
    {
        if (Open.Count > 0)
        {
            foreach (var f in Open.ToList())
            {
                f.Show();
                if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
                f.Activate();
            }
            return;
        }
        List<TerminalWindowConfig> saved;
        lock (Bench.Config.TerminalWindows) saved = Bench.Config.TerminalWindows.Where(w => w.Open).ToList();
        if (saved.Count == 0) NewWindow(null);
        else foreach (var w in saved) Reopen(w);
    }

    /// A new window, showing `port` if one is named.
    public static TerminalForm NewWindow(string? port, Point? near = null)
    {
        var w = new TerminalWindowConfig();
        if (port != null) w.Ports.Add(port.ToUpperInvariant());
        if (near is { } p) { w.X = p.X + 40; w.Y = p.Y + 40; }
        lock (Bench.Config.TerminalWindows) Bench.Config.TerminalWindows.Add(w);
        return Reopen(w);
    }

    static TerminalForm Reopen(TerminalWindowConfig w)
    {
        w.Open = true;
        var f = new TerminalForm(w);
        f.Show();
        f.Activate();
        Bench.Save();
        return f;
    }

    /// At exit: the windows stay marked open, so the next start brings them back.
    public static void CloseAllForExit()
    {
        foreach (var f in Open.ToList()) { f._quitting = true; f.Close(); }
    }

    /// Whether any window has a tab for this port.
    static bool IsShown(string port) => Open.Any(f => f.W.Ports.Contains(port));

    TerminalForm(TerminalWindowConfig w)
    {
        W = w;
        Open.Add(this);
        Icon = TrayIcon.Make();
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = w.X >= 0 ? new Point(w.X, w.Y) : new Point(area.X + 40 + 30 * (Open.Count - 1), area.Y + 40 + 30 * (Open.Count - 1));
        if (w.W > 0 && w.H > 0) Size = new Size(w.W, w.H);
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Bounds))) Location = new Point(area.X + 40, area.Y + 40);

        _rec.Font = new Font(_status.Font, FontStyle.Bold);
        _bar.Items.Add(new ToolStripLabel("Port"));
        _bar.Items.Add(_port);
        _bar.Items.Add(_open);
        _bar.Items.Add(_settings);
        _bar.Items.Add(_record);
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.Add(new ToolStripButton("Reset machine", null, (_, _) => ResetMachine()) { ToolTipText = "Send the reset sequence (ResetSequence in bench.json; by default the CORSAC kernel's ESC ESC ESC RESET)" });
        _bar.Items.Add(new ToolStripButton("Break", null, (_, _) => SendBreak()) { ToolTipText = "Hold the line in BREAK for 250 ms" });
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.Add(_size);
        _bar.Items.Add(new ToolStripButton("New window", null, (_, _) => NewWindow(null, Location)));
        _bar.Items.Add(new ToolStripButton("Screens", null, (_, _) => TrayIcon.ShowScreens()));
        _status.Items.Add(_rec);
        _status.Items.Add(_state);
        _status.Items.Add(_clients);
        Controls.Add(_tabs);
        Controls.Add(_bar);
        Controls.Add(_status);

        _port.DropDown += (_, _) => FillPorts();
        _port.SelectedIndexChanged += (_, _) => { if (!_filling && _port.SelectedItem is PortItem p) ShowLine(Bench.Line(p.Name)); };
        _open.Click += (_, _) => ToggleOpen();
        _settings.DropDownOpening += (_, _) => BuildSettings();
        _record.DropDownOpening += (_, _) => BuildRecord();
        _size.DropDownOpening += (_, _) => BuildSize();
        _tabs.SelectedIndexChanged += (_, _) => { FillPorts(); UpdateStatus(); Current?.Focus(); };
        _tabs.MouseUp += TabMouseUp;
        _tick.Tick += (_, _) => UpdateStatus();
        _tick.Start();
        Bench.LinesChanged += LinesChanged;
        Recordings.Changed += RecordingsChanged;

        SyncTabs();
        ShowLine(Bench.Line(W.Ports.Count == 0 ? null : W.Ports[0]));
        Shown += (_, _) => { if (W.W <= 0) SizeFor(Bench.Config.Cols, Bench.Config.Rows); };
        ResizeEnd += (_, _) => Remember();
        Move += (_, _) => { if (WindowState == FormWindowState.Normal && Visible) { W.X = Left; W.Y = Top; } };
    }

    void LinesChanged() { if (IsHandleCreated && !IsDisposed) BeginInvoke(SyncTabs); }
    void RecordingsChanged() { if (IsHandleCreated && !IsDisposed) BeginInvoke(SyncTabs); }

    sealed record PortItem(string Name, string Text)
    {
        public override string ToString() => Text;
    }

    TermView? Current => _tabs.SelectedTab?.Controls.OfType<TermView>().FirstOrDefault();

    bool _filling;

    /// The port list, rebuilt: its text says which are open, so it is redone
    /// whenever a port opens or closes and whenever the tab changes.
    void FillPorts()
    {
        _filling = true;
        try { FillPortsCore(); }
        finally { _filling = false; }
    }

    void FillPortsCore()
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

    /// <summary>
    /// A tab for each of this window's ports. The first window also takes any
    /// port that is open, or has been heard from, and that no window shows.
    /// </summary>
    void SyncTabs()
    {
        if (IsDisposed) return;
        if (Open.Count > 0 && Open[0] == this)
        {
            List<Line> orphans;
            lock (Bench.Lines) orphans = Bench.Lines.Values.Where(l => (l.IsOpen || l.End > 0 || l.Name == Bench.DefaultPort) && !IsShown(l.Name)).ToList();
            foreach (var l in orphans.OrderBy(l => l.Name.Length).ThenBy(l => l.Name)) W.Ports.Add(l.Name);
        }
        foreach (var name in W.Ports.ToList())
            if (!_tabs.TabPages.ContainsKey(name)) AddTab(Bench.Line(name));
        foreach (TabPage p in _tabs.TabPages.Cast<TabPage>().ToList())
            if (!W.Ports.Contains(p.Name)) RemoveTab(p);
        List<Recording> active;
        lock (Recordings.Active) active = Recordings.Active.ToList();
        foreach (TabPage p in _tabs.TabPages)
            p.Text = p.Name + (Bench.Lines.TryGetValue(p.Name, out var l) && l.IsOpen ? "" : " (closed)")
                   + (active.Any(r => r.C.Ports.Contains(p.Name)) ? " ●" : "");
        FillPorts();
        UpdateStatus();
    }

    void AddTab(Line l)
    {
        if (!W.Ports.Contains(l.Name)) W.Ports.Add(l.Name);
        var view = new TermView(l) { Dock = DockStyle.Fill };
        view.Status += s => _state.Text = s;
        // DECCOLM: the machine asks for 80 or 132 columns and the window follows.
        l.Term.ColumnsWanted = cols => { if (IsHandleCreated) BeginInvoke(() => { if (Current?.Line == l) SizeFor(cols, l.Term.Rows); }); };
        var page = new TabPage(l.Name) { Name = l.Name };
        page.Controls.Add(view);
        _tabs.TabPages.Add(page);
        Bench.Save();
    }

    void RemoveTab(TabPage page)
    {
        W.Ports.Remove(page.Name);
        _tabs.TabPages.Remove(page);
        foreach (Control c in page.Controls.Cast<Control>().ToList()) c.Dispose();
        page.Dispose();
        Bench.Save();
    }

    void ShowLine(Line l)
    {
        if (!_tabs.TabPages.ContainsKey(l.Name)) AddTab(l);
        _tabs.SelectedTab = _tabs.TabPages[l.Name];
        SyncTabs();
        Current?.Focus();
    }

    /// Right-click on a tab: that tab's menu.
    void TabMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        for (int i = 0; i < _tabs.TabCount; i++)
        {
            if (!_tabs.GetTabRect(i).Contains(e.Location)) continue;
            var page = _tabs.TabPages[i];
            var name = page.Name;
            var m = new ContextMenuStrip();
            m.Items.Add($"Record {name}...", null, (_, _) => RecordDialog.Ask(this, new[] { name }));
            m.Items.Add("Move to a new window", null, (_, _) => { RemoveTab(page); NewWindow(name, Location); if (_tabs.TabCount == 0) Close(); });
            m.Items.Add("Also show in a new window", null, (_, _) => NewWindow(name, Location));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(_tabs.TabCount > 1 ? "Close this tab (the port stays as it is)" : "Close this tab and window (the port stays as it is)", null, (_, _) =>
            {
                RemoveTab(page);
                if (_tabs.TabCount == 0) Close();
            });
            m.Show(_tabs, e.Location);
            return;
        }
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
        _settings.DropDownItems.Add(Pick("Speed", new[] { 300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 }.Select(b => (b.ToString(), b)), st.Baud, (s, v) => s.Baud = v));
        _settings.DropDownItems.Add(Pick("Data bits", new[] { 8, 7, 6, 5 }.Select(b => (b.ToString(), b)), st.ByteSize, (s, v) => s.ByteSize = v));
        _settings.DropDownItems.Add(Pick("Parity", new[] { ("None", Parity.None), ("Even", Parity.Even), ("Odd", Parity.Odd), ("Mark", Parity.Mark), ("Space", Parity.Space) }, st.Parity, (s, v) => s.Parity = v));
        _settings.DropDownItems.Add(Pick("Stop bits", new[] { ("1", StopBits.One), ("1.5", StopBits.OnePointFive), ("2", StopBits.Two) }, st.StopBits, (s, v) => s.StopBits = v));
        _settings.DropDownItems.Add(Pick("Flow control", new[] { ("None", 0), ("RTS/CTS", 1), ("XON/XOFF", 2) }, st.RtsCts ? 1 : st.XonXoff ? 2 : 0, (s, v) => { s.RtsCts = v == 1; s.XonXoff = v == 2; }));
        _settings.DropDownItems.Add(new ToolStripSeparator());
        _settings.DropDownItems.Add(new ToolStripMenuItem("Default port for the assistants", null, (_, _) => { Bench.DefaultPort = l.Name; Bench.Save(); }) { Checked = Bench.DefaultPort == l.Name });
        _settings.DropDownItems.Add(new ToolStripMenuItem("Open the bench's log of this port", null, (_, _) => OpenPath(l.LogPath)));
    }

    void BuildRecord()
    {
        _record.DropDownItems.Clear();
        var l = Current?.Line;
        if (l != null)
            _record.DropDownItems.Add(new ToolStripMenuItem($"Record {l.Name}...", null, (_, _) => RecordDialog.Ask(this, new[] { l.Name })) { Font = new Font(_record.Font, FontStyle.Bold) });
        _record.DropDownItems.Add(new ToolStripMenuItem("Record several ports into one file...", null, (_, _) => RecordDialog.Ask(this, W.Ports.ToArray())));
        List<Recording> active;
        lock (Recordings.Active) active = Recordings.Active.ToList();
        if (active.Count > 0)
        {
            _record.DropDownItems.Add(new ToolStripSeparator());
            foreach (var r in active)
            {
                var rec = r;
                _record.DropDownItems.Add(new ToolStripMenuItem("Stop " + rec.Describe(), null, (_, _) => Recordings.Stop(rec)));
            }
            _record.DropDownItems.Add(new ToolStripMenuItem("Stop all recordings", null, (_, _) => Recordings.StopAll()));
        }
        _record.DropDownItems.Add(new ToolStripSeparator());
        _record.DropDownItems.Add(new ToolStripMenuItem("Open the recordings folder", null, (_, _) =>
        {
            Directory.CreateDirectory(Recordings.Folder);
            OpenPath(Recordings.Folder);
        }));
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
                foreach (var f in Open) foreach (var t in f.AllViews()) t.SetFont(Bench.Config.Font, pt);
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

    /// <summary>This window's size and place, and the terminal size a new window opens at.</summary>
    void Remember()
    {
        if (WindowState != FormWindowState.Normal) return;
        W.X = Left; W.Y = Top; W.W = Width; W.H = Height;
        var vt = Current?.Line.Term;
        if (vt != null) { Bench.Config.Cols = vt.Cols; Bench.Config.Rows = vt.Rows; }
        Bench.Save();
    }

    void UpdateStatus()
    {
        if (IsDisposed) return;
        var l = Current?.Line;
        if (l != null)
        {
            _open.Text = l.IsOpen ? "Close" : "Open";
            _state.Text = $"{l.Describe()}   {l.End:N0} bytes received" +
                          (l.OpenedBy != "" ? $"   opened by {l.OpenedBy}" : "") +
                          (l.ConversationHolder != "" ? $"   running a command for {l.ConversationHolder}" : "") +
                          (l.Error != "" ? "   " + l.Error : "") +
                          (Current!.Note != "" ? "   " + Current.Note : "");
            List<Recording> here;
            lock (Recordings.Active) here = Recordings.Active.Where(r => r.C.Ports.Contains(l.Name)).ToList();
            _rec.Text = here.Count == 0 ? "" : here.Count == 1 ? "● REC" : $"● REC x{here.Count}";
            _rec.ToolTipText = string.Join("\n", here.Select(r => r.Describe()));
        }
        Text = "CORSAC bench - " + (W.Ports.Count == 0 ? "terminal" : string.Join(", ", W.Ports));
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

    public static void OpenPath(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // THE LAST WINDOW HIDES rather than closing, so the tray icon brings
        // back the same one with its tabs; any other closed by hand is gone.
        if (e.CloseReason == CloseReason.UserClosing && !_quitting && Open.Count == 1 && _tabs.TabCount > 0)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Open.Remove(this);
        Bench.LinesChanged -= LinesChanged;
        Recordings.Changed -= RecordingsChanged;
        _tick.Dispose();
        if (!_quitting)
            lock (Bench.Config.TerminalWindows) Bench.Config.TerminalWindows.Remove(W);
        Bench.Save();
        // A port this window was the only one to show goes back to the first window.
        foreach (var f in Open) if (f.IsHandleCreated) f.BeginInvoke(f.SyncTabs);
        base.OnFormClosed(e);
    }
}

/// Start a recording: which ports, where, in what form.
public sealed class RecordDialog : Form
{
    readonly CheckedListBox _ports = new() { CheckOnClick = true, Height = 110, Dock = DockStyle.Fill };
    readonly TextBox _path = new() { Dock = DockStyle.Fill };
    readonly ComboBox _format = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly CheckBox _sent = new() { Text = "Include what is sent to the port, marked with who sent it", Checked = true, AutoSize = true };
    readonly CheckBox _append = new() { Text = "Add to the file if it exists (otherwise replace it)", Checked = true, AutoSize = true };

    public static void Ask(IWin32Window owner, IEnumerable<string> ports)
    {
        using var d = new RecordDialog(ports);
        if (d.ShowDialog(owner) != DialogResult.OK) return;
        var chosen = d._ports.CheckedItems.Cast<string>().ToList();
        try
        {
            var rec = Recordings.Start(chosen, d._path.Text, (RecordFormat)d._format.SelectedIndex, d._sent.Checked, d._append.Checked, "window");
            TrayIcon.Tell("Recording", rec.Describe());
        }
        catch (Exception e) { MessageBox.Show(owner, e.Message, "Recording did not start", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    RecordDialog(IEnumerable<string> ports)
    {
        Text = "Record serial ports";
        Icon = TrayIcon.Make();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        var want = ports.Select(p => p.ToUpperInvariant()).ToHashSet();
        foreach (var p in Bench.ComPorts().Select(p => p.ToUpperInvariant()).Concat(Bench.Lines.Keys).Distinct().OrderBy(p => p.Length).ThenBy(p => p))
            _ports.Items.Add(p, want.Contains(p));
        _format.Items.AddRange(new object[] { "Raw bytes, exactly as received", "Plain text (escape sequences and CRs removed)", "Plain text with a timestamp on every line" });
        _format.SelectedIndex = (int)RecordFormat.Timestamped;
        _format.SelectedIndexChanged += (_, _) => { _sent.Enabled = _format.SelectedIndex != (int)RecordFormat.Raw; Suggest(); };
        _ports.ItemCheck += (_, _) => BeginInvoke(Suggest);

        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            Directory.CreateDirectory(Recordings.Folder);
            using var s = new SaveFileDialog { Filter = "Logs|*.log;*.txt|Raw captures|*.bin|All files|*.*", FileName = Path.GetFileName(_path.Text), InitialDirectory = Recordings.Folder, OverwritePrompt = false };
            if (s.ShowDialog(this) == DialogResult.OK) { _path.Text = s.FileName; _path.Modified = true; }
        };
        var ok = new Button { Text = "Record", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        AcceptButton = ok;
        CancelButton = cancel;

        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void Row(string label, Control c, Control? extra = null)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 6, 8, 0) });
            grid.Controls.Add(c);
            grid.Controls.Add(extra ?? new Label { AutoSize = true });
        }
        Row("Ports", _ports);
        Row("File", _path, browse);
        Row("Form", _format);
        Row("", _sent);
        Row("", _append);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        Row("", buttons);
        Controls.Add(grid);
        Suggest();
    }

    /// A file name from the ports and the time, until one is typed.
    void Suggest()
    {
        if (_path.Modified) return;
        var chosen = _ports.CheckedItems.Cast<string>().ToList();
        _path.Text = Recordings.Resolve(null, chosen.Count > 0 ? chosen : new List<string> { "COM" }, (RecordFormat)Math.Max(0, _format.SelectedIndex));
        _path.Modified = false;
    }
}
