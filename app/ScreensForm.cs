// The capture devices, any or all of them at once. Each view has its own
// settings (right-click it); double-click one to show it alone and again to
// go back to all of them.

using System.Drawing;
using System.Windows.Forms;

namespace CorsacBench;

public sealed class ScreensForm : Form
{
    readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden };
    readonly ToolStripDropDownButton _devices = new("Devices");
    readonly ToolStripDropDownButton _layout = new("Layout");
    readonly TableLayoutPanel _grid = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 24, 24), Margin = Padding.Empty };
    readonly List<ScreenView> _views = new();
    ScreenView? _solo;

    public ScreensForm()
    {
        Text = "CORSAC bench - screens";
        Icon = TrayIcon.Make();
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Bounds = new Rectangle(area.X + 60, area.Y + 60, 1000, 800);
        _bar.Items.Add(_devices);
        _bar.Items.Add(_layout);
        _bar.Items.Add(new ToolStripButton("Refresh devices", null, (_, _) => BuildDeviceMenu()));
        _bar.Items.Add(new ToolStripLabel("right-click a picture for its settings; double-click to show it alone") { ForeColor = SystemColors.GrayText, Alignment = ToolStripItemAlignment.Right });
        Controls.Add(_grid);
        Controls.Add(_bar);
        _devices.DropDown.Closing += (_, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
        foreach (var (name, key) in new[] { ("Grid", "grid"), ("Side by side", "row"), ("Stacked", "column") })
            _layout.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => { Bench.Config.ScreensLayout = key; Bench.Save(); Arrange(); }) { Tag = key });
        _layout.DropDownOpening += (_, _) => { foreach (ToolStripMenuItem i in _layout.DropDownItems) i.Checked = (string)i.Tag! == Bench.Config.ScreensLayout; };
        Shown += (_, _) => BuildDeviceMenu();
    }

    static ScreenSettings SettingsFor(string device)
    {
        lock (Bench.Config.Screens)
        {
            if (!Bench.Config.Screens.TryGetValue(device, out var s))
                Bench.Config.Screens[device] = s = new ScreenSettings { Shown = device == Bench.DefaultVga };
            return s;
        }
    }

    void BuildDeviceMenu()
    {
        _devices.DropDownItems.Clear();
        List<string> all;
        try { all = Vga.Devices(); }
        catch (Exception e)
        {
            _devices.DropDownItems.Add(new ToolStripMenuItem("could not list devices: " + e.Message) { Enabled = false });
            return;
        }
        foreach (var d in all)
        {
            var s = SettingsFor(d);
            var item = new ToolStripMenuItem(d) { Checked = s.Shown, CheckOnClick = true };
            item.CheckedChanged += (_, _) => { s.Shown = item.Checked; Bench.Save(); Sync(all); };
            _devices.DropDownItems.Add(item);
        }
        _devices.DropDownItems.Add(new ToolStripSeparator());
        _devices.DropDownItems.Add(new ToolStripMenuItem("Show all", null, (_, _) => { foreach (var d in all) SettingsFor(d).Shown = true; Bench.Save(); BuildDeviceMenu(); }));
        _devices.DropDownItems.Add(new ToolStripMenuItem("Only the vga_capture device", null, (_, _) => { foreach (var d in all) SettingsFor(d).Shown = d == Bench.DefaultVga; Bench.Save(); BuildDeviceMenu(); }));
        if (!all.Any(d => SettingsFor(d).Shown) && all.Count > 0)
            SettingsFor(all.FirstOrDefault(d => d == Bench.DefaultVga) ?? all[0]).Shown = true;
        Sync(all);
    }

    /// <summary>Makes the views match the devices ticked.</summary>
    void Sync(List<string> all)
    {
        foreach (var v in _views.Where(v => !all.Contains(v.Device) || !SettingsFor(v.Device).Shown).ToList())
        {
            _views.Remove(v);
            if (_solo == v) _solo = null;
            v.Dispose();
        }
        foreach (var d in all.Where(d => SettingsFor(d).Shown && _views.All(v => v.Device != d)))
        {
            var v = new ScreenView(d, SettingsFor(d)) { Dock = DockStyle.Fill, Margin = new Padding(1) };
            v.ShapeChanged += FitTo;
            v.Solo += s => { _solo = _solo == s ? null : s; Arrange(); };
            _views.Add(v);
        }
        _views.Sort((a, b) => all.IndexOf(a.Device).CompareTo(all.IndexOf(b.Device)));
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
        (int cols, int rows) = Bench.Config.ScreensLayout switch
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
        if (WindowState != FormWindowState.Normal) return;
        var shown = _solo != null ? new List<ScreenView> { _solo } : _views;
        if (shown.Count != 1 || shown[0] != v) return;
        var chrome = Size - v.ClientSize;
        var area = Screen.FromControl(this).WorkingArea;
        var size = new Size(Math.Min(wanted.Width + chrome.Width, area.Width), Math.Min(wanted.Height + chrome.Height, area.Height));
        var at = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - size.Width)), Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - size.Height)));
        Bounds = new Rectangle(at, size);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing hides; the views stop asking for frames, and the devices are let go.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            foreach (var v in _views) v.Dispose();
            _views.Clear();
            _solo = null;
            _grid.Controls.Clear();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && _views.Count == 0 && IsHandleCreated) BuildDeviceMenu();
    }
}
