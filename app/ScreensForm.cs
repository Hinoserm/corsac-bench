// A screens window: any of the capture devices, one or many, live. There can
// be as many of these windows as wanted, each with its own devices, layout,
// size and place, all remembered and opened again at the next start. Each
// picture's settings are its own (right-click it) and belong to the device.
// Double-click a picture to show it alone in its window, and again to go back.

using System.Drawing;
using System.Windows.Forms;

namespace CorsacBench;

public sealed class ScreensForm : Form
{
    public static readonly List<ScreensForm> Open = new();

    public readonly ScreenWindowConfig W;
    readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden };
    readonly ToolStripDropDownButton _devices = new("Devices");
    readonly ToolStripDropDownButton _layout = new("Layout");
    readonly TableLayoutPanel _grid = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 24, 24), Margin = Padding.Empty };
    readonly List<ScreenView> _views = new();
    List<string> _all = new();
    ScreenView? _solo;
    /// The picture last clicked: the one Screenshot copies when several are shown.
    ScreenView? _chosen;
    // THE BUTTON BAR, under the menu: the common actions, on the picture last
    // clicked (or the only one shown).
    readonly ToolStrip _tools = new() { GripStyle = ToolStripGripStyle.Hidden };
    readonly ToolStripButton _shot = Button("Screenshot", '\uE722', "Copy the picture to the clipboard, at the resolution it arrives");
    readonly ToolStripButton _save = Button("Save picture", '\uE74E', "Save the picture as a PNG, at the resolution it arrives");
    readonly ToolStripButton _pause = Button("Pause", '\uE769', "Hold the picture still");
    readonly ToolStripButton _full = Button("Full screen", '\uE740', "The whole monitor for the pictures (F11; Esc or F11 to leave)");
    FormWindowState _wasState;
    Rectangle _wasBounds;
    bool _isFull;
    readonly ToolStripLabel _hint = new("right-click a picture for its settings; double-click to show it alone") { ForeColor = SystemColors.GrayText, Alignment = ToolStripItemAlignment.Right };
    readonly System.Windows.Forms.Timer _hintBack = new() { Interval = 3000 };
    bool _quitting;

    /// <summary>A new, empty window, beside `near` if given.</summary>
    public static ScreensForm NewWindow(Point? near = null)
    {
        var w = new ScreenWindowConfig();
        if (near is { } p) { w.X = p.X + 40; w.Y = p.Y + 40; }
        lock (Bench.Config.ScreenWindows) Bench.Config.ScreenWindows.Add(w);
        return Reopen(w);
    }

    /// <summary>A window from its remembered settings.</summary>
    static ScreensForm Reopen(ScreenWindowConfig w)
    {
        w.Open = true;
        var f = new ScreensForm(w);
        f.Show();
        Bench.Save();
        return f;
    }

    /// <summary>A window showing one device alone.</summary>
    public static ScreensForm ShowDevice(string device, Point? near = null)
    {
        var w = new ScreenWindowConfig { Devices = new() { device }, W = 800, H = 640 };
        if (near is { } p) { w.X = p.X + 40; w.Y = p.Y + 40; }
        lock (Bench.Config.ScreenWindows) Bench.Config.ScreenWindows.Add(w);
        return Reopen(w);
    }

    /// <summary>The windows that were open at the last exit, or a new one.</summary>
    public static void ShowAll()
    {
        if (Open.Count > 0)
        {
            foreach (var f in Open.ToList())
            {
                if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
                f.Activate();
            }
            return;
        }
        List<ScreenWindowConfig> saved;
        lock (Bench.Config.ScreenWindows) saved = Bench.Config.ScreenWindows.Where(w => w.Open).ToList();
        if (saved.Count == 0) NewWindow();
        else foreach (var w in saved) Reopen(w);
    }

    /// <summary>At exit: the windows stay marked open, so the next start brings them back.</summary>
    public static void CloseAllForExit()
    {
        foreach (var f in Open.ToList()) { f._quitting = true; f.Close(); }
    }

    ScreensForm(ScreenWindowConfig w)
    {
        W = w;
        Open.Add(this);
        Icon = TrayIcon.Make();
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen!.WorkingArea;
        var bounds = new Rectangle(w.X < 0 ? area.X + 60 + 30 * (Open.Count - 1) : w.X, w.Y < 0 ? area.Y + 60 + 30 * (Open.Count - 1) : w.Y, w.W, w.H);
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds))) bounds.Location = new Point(area.X + 60, area.Y + 60);
        Bounds = bounds;
        _bar.Items.Add(_devices);
        _bar.Items.Add(_layout);
        _bar.Items.Add(new ToolStripButton("New window", null, (_, _) => NewWindow(Location)));
        _bar.Items.Add(_hint);
        _tools.Items.AddRange(new ToolStripItem[] { _shot, _save, new ToolStripSeparator(), _pause, _full });
        _shot.Click += (_, _) => Copy(Target());
        _save.Click += (_, _) => Target()?.SaveAs();
        _pause.Click += (_, _) => { if (Target() is { } v) v.Paused = !v.Paused; };
        _full.Click += (_, _) => FullScreen(!_isFull);
        KeyPreview = true;
        _hintBack.Tick += (_, _) => { _hintBack.Stop(); _hint.Text = "right-click a picture for its settings; double-click to show it alone"; _hint.ForeColor = SystemColors.GrayText; };
        Controls.Add(_grid);
        Controls.Add(_tools);
        Controls.Add(_bar);
        // Ticking devices keeps the menu open, so several can be picked at once.
        _devices.DropDown.Closing += (_, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
        _devices.DropDownOpening += (_, _) => BuildDeviceMenu();
        foreach (var (name, key) in new[] { ("Grid", "grid"), ("Side by side", "row"), ("Stacked", "column") })
            _layout.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => { W.Layout = key; Bench.Save(); Arrange(); }) { Tag = key });
        _layout.DropDownOpening += (_, _) => { foreach (ToolStripMenuItem i in _layout.DropDownItems) i.Checked = (string)i.Tag! == W.Layout; };
        Shown += (_, _) => { Refresh(); Sync(); };
        ResizeEnd += (_, _) => Remember();
        Move += (_, _) => { if (WindowState == FormWindowState.Normal && Visible) Remember(); };
    }

    void Remember()
    {
        if (WindowState != FormWindowState.Normal || _isFull) return;
        W.X = Left; W.Y = Top; W.W = Width; W.H = Height;
    }

    static ScreenSettings SettingsFor(string device)
    {
        lock (Bench.Config.Screens)
        {
            if (!Bench.Config.Screens.TryGetValue(device, out var s)) Bench.Config.Screens[device] = s = new ScreenSettings();
            return s;
        }
    }

    new void Refresh()
    {
        try { _all = Vga.Devices(); }
        catch { _all = new(); }
        if (W.Devices.Count == 0 && _all.Count > 0)
            W.Devices.Add(_all.FirstOrDefault(d => d == Bench.DefaultVga) ?? _all[0]);
    }

    void BuildDeviceMenu()
    {
        Refresh();
        _devices.DropDownItems.Clear();
        foreach (var d in _all)
        {
            var item = new ToolStripMenuItem(Short(d)) { Checked = W.Devices.Contains(d), CheckOnClick = true };
            item.CheckedChanged += (_, _) =>
            {
                if (item.Checked) { if (!W.Devices.Contains(d)) W.Devices.Add(d); }
                else W.Devices.Remove(d);
                Bench.Save();
                Sync();
            };
            _devices.DropDownItems.Add(item);
        }
        if (_all.Count == 0) _devices.DropDownItems.Add(new ToolStripMenuItem("no capture devices") { Enabled = false });
        _devices.DropDownItems.Add(new ToolStripSeparator());
        _devices.DropDownItems.Add(new ToolStripMenuItem("Show all here", null, (_, _) => { W.Devices = _all.ToList(); Bench.Save(); Sync(); _devices.HideDropDown(); }));
        _devices.DropDownItems.Add(new ToolStripMenuItem("Each in its own window", null, (_, _) =>
        {
            _devices.HideDropDown();
            if (_all.Count == 0) return;
            var keep = W.Devices.FirstOrDefault(_all.Contains) ?? _all[0];
            var others = _all.Where(d => d != keep).ToList();
            W.Devices = new() { keep };
            Sync();
            int i = 0;
            foreach (var d in others) ShowDevice(d, new Point(Left + 40 * i, Top + 40 * i++));
        }));
    }

    static string Short(string device) => device.StartsWith("Video (") && device.EndsWith(")") ? device[7..^1] : device;

    ScreenView? Target() => Showing().Contains(_chosen!) ? _chosen : Showing().FirstOrDefault();

    /// <summary>The buttons show the state of the picture they act on.</summary>
    void ShowState()
    {
        bool paused = Target()?.Paused == true;
        _pause.Text = paused ? "Resume" : "Pause";
        _pause.Image = Glyph(paused ? '\uE768' : '\uE769');
        _pause.Checked = paused;
        _pause.ToolTipText = paused ? "Let the picture move again" : "Hold the picture still";
    }

    static ToolStripButton Button(string text, char symbol, string tip) =>
        new(text, Glyph(symbol)) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, ToolTipText = tip };

    /// <summary>The pictures on the whole monitor, without the window's frame or bars.</summary>
    void FullScreen(bool on)
    {
        if (on == _isFull) return;
        _isFull = on;
        if (on)
        {
            _wasState = WindowState;
            _wasBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            _bar.Visible = _tools.Visible = false;
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            _bar.Visible = _tools.Visible = true;
            Bounds = _wasBounds;
            WindowState = _wasState;
        }
        _full.Checked = on;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11 || e.KeyCode == Keys.Escape && _isFull) { FullScreen(!_isFull); e.Handled = true; }
        base.OnKeyDown(e);
    }

    List<ScreenView> Showing() => _solo != null ? new List<ScreenView> { _solo } : _views;

    /// <summary>A Windows symbol (Segoe MDL2 Assets, in every Windows 10 and 11) as a menu image.</summary>
    static Bitmap Glyph(char symbol)
    {
        // Drawn large; the tool strip scales it to its image size for the monitor's DPI.
        const int size = 32;
        var b = new Bitmap(size, size);
        using var g = Graphics.FromImage(b);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe MDL2 Assets", size * 0.75f, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(SystemColors.ControlText);
        using var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(symbol.ToString(), font, ink, new RectangleF(0, 0, size, size), centre);
        return b;
    }

    /// <summary>The picture, as captured, onto the clipboard; the bar says so.</summary>
    void Copy(ScreenView? v)
    {
        using var b = v?.Snapshot();
        if (v == null || b == null) { Say("no picture to copy", Color.Orange); return; }
        Clipboard.SetImage(b);
        Say($"copied {Short(v.Device)}, {b.Width}x{b.Height}", SystemColors.ControlText);
    }

    void Say(string text, Color colour)
    {
        _hint.Text = text;
        _hint.ForeColor = colour;
        _hintBack.Stop();
        _hintBack.Start();
    }

    /// <summary>Makes the views match the devices this window shows.</summary>
    void Sync()
    {
        foreach (var v in _views.Where(v => !W.Devices.Contains(v.Device)).ToList())
        {
            _views.Remove(v);
            if (_solo == v) _solo = null;
            if (_chosen == v) _chosen = null;
            v.Dispose();
        }
        foreach (var d in W.Devices.Where(d => _views.All(v => v.Device != d)))
        {
            var v = new ScreenView(d, SettingsFor(d)) { Dock = DockStyle.Fill, Margin = new Padding(1) };
            v.ShapeChanged += FitTo;
            v.Solo += s => { _solo = _solo == s ? null : s; Arrange(); };
            v.PopOut += s => ShowDevice(s.Device, Location);
            v.Chosen += s => { _chosen = s; ShowState(); };
            v.PausedChanged += _ => ShowState();
            _views.Add(v);
        }
        _views.Sort((a, b) => W.Devices.IndexOf(a.Device).CompareTo(W.Devices.IndexOf(b.Device)));
        Text = "CORSAC bench - " + (W.Devices.Count == 1 ? Short(W.Devices[0]) : $"{W.Devices.Count} screens");
        Arrange();
    }

    void Arrange()
    {
        _grid.SuspendLayout();
        _grid.Controls.Clear();
        _grid.ColumnStyles.Clear();
        _grid.RowStyles.Clear();
        var shown = _solo != null ? new List<ScreenView> { _solo } : _views;
        foreach (var v in _views) v.Visible = shown.Contains(v);
        int n = Math.Max(1, shown.Count);
        (int cols, int rows) = W.Layout switch
        {
            "row" => (n, 1),
            "column" => (1, n),
            _ => ((int)Math.Ceiling(Math.Sqrt(n)), (int)Math.Ceiling(n / Math.Ceiling(Math.Sqrt(n)))),
        };
        _grid.ColumnCount = cols;
        _grid.RowCount = rows;
        for (int i = 0; i < cols; i++) _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
        for (int i = 0; i < rows; i++) _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
        for (int i = 0; i < shown.Count; i++) _grid.Controls.Add(shown[i], i % cols, i / cols);
        _grid.ResumeLayout();
    }

    /// <summary>A picture changed shape and wants the window to follow: only when it is the one shown.</summary>
    void FitTo(ScreenView v, Size wanted)
    {
        if (WindowState != FormWindowState.Normal || _isFull) return;
        var shown = _solo != null ? new List<ScreenView> { _solo } : _views;
        if (shown.Count != 1 || shown[0] != v) return;
        var chrome = Size - v.ClientSize;
        var area = Screen.FromControl(this).WorkingArea;
        var size = new Size(Math.Min(wanted.Width + chrome.Width, area.Width), Math.Min(wanted.Height + chrome.Height, area.Height));
        var at = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - size.Width)), Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - size.Height)));
        Bounds = new Rectangle(at, size);
        Remember();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Open.Remove(this);
        foreach (var v in _views) v.Dispose();
        _views.Clear();
        // Closed by hand, it is forgotten; closed by the bench exiting, it comes back next time.
        if (!_quitting && e.CloseReason == CloseReason.UserClosing)
            lock (Bench.Config.ScreenWindows) Bench.Config.ScreenWindows.Remove(W);
        Bench.Save();
        base.OnFormClosed(e);
    }
}
