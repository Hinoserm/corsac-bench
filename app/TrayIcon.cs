// The bench lives in the notification area: the icon opens the terminal, its
// menu opens and closes ports and shows the screens.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CorsacBench;

public sealed class TrayIcon : ApplicationContext
{
    static TrayIcon? _it;
    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu = new();
    readonly Control _ui = new();          // marshals work onto the UI thread

    public TrayIcon()
    {
        _it = this;
        _ui.CreateControl();
        _icon = new NotifyIcon { Icon = Make(), Text = "CORSAC bench", ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowTerminal(); };
        _menu.Opening += (_, _) => BuildMenu();
        Bench.ClientsChanged += () => Post(UpdateTip);
        Bench.LinesChanged += () => Post(UpdateTip);
        UpdateTip();
        if (Mcp.Error != "")
            _icon.ShowBalloonTip(10000, "CORSAC bench", Mcp.Error, ToolTipIcon.Error);
    }

    public static void Post(Action a)
    {
        var ui = _it?._ui;
        if (ui != null && ui.IsHandleCreated) ui.BeginInvoke(a);
    }

    /// <summary>A small screen with a prompt on it, drawn rather than shipped.</summary>
    public static Icon Make()
    {
        using var b = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var frame = new SolidBrush(Color.FromArgb(40, 40, 48));
            using var glass = new SolidBrush(Color.FromArgb(10, 60, 30));
            using var green = new Pen(Color.FromArgb(80, 240, 120), 3f);
            g.FillRectangle(frame, 1, 3, 30, 22);
            g.FillRectangle(glass, 4, 6, 24, 16);
            g.DrawLines(green, new[] { new Point(8, 10), new Point(13, 14), new Point(8, 18) });
            g.DrawLine(green, 15, 18, 23, 18);
            g.FillRectangle(frame, 11, 25, 10, 3);
            g.FillRectangle(frame, 7, 28, 18, 3);
        }
        return Icon.FromHandle(b.GetHicon());
    }

    void UpdateTip()
    {
        int open, sessions;
        lock (Bench.Lines) open = Bench.Lines.Values.Count(l => l.IsOpen);
        lock (Bench.Clients) sessions = Bench.Clients.Count;
        var tip = $"CORSAC bench :{Mcp.Port}\n{open} port{(open == 1 ? "" : "s")} open, {sessions} session{(sessions == 1 ? "" : "s")}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    public static void ShowTerminal() => _it?.ShowTerminalCore();
    public static void ShowScreens() => _it?.ShowScreensCore();

    void ShowTerminalCore() => TerminalForm.ShowAll();

    /// A notification from the tray icon.
    public static void Tell(string title, string text) => Post(() => _it?._icon.ShowBalloonTip(5000, title, text.Length > 250 ? text[..250] : text, ToolTipIcon.Info));

    void ShowScreensCore() => ScreensForm.ShowAll();

    void BuildMenu()
    {
        _menu.Items.Clear();
        var terminals = new ToolStripMenuItem("Terminal") { Font = new Font(_menu.Font, FontStyle.Bold) };
        terminals.DropDownItems.Add(new ToolStripMenuItem("Show the terminal windows", null, (_, _) => ShowTerminal()) { Font = new Font(_menu.Font, FontStyle.Bold) });
        terminals.DropDownItems.Add(new ToolStripMenuItem("New terminal window", null, (_, _) => TerminalForm.NewWindow(null)));
        terminals.DropDownItems.Add(new ToolStripSeparator());
        foreach (var p in Bench.ComPorts().Concat(Bench.Lines.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ThenBy(p => p))
        {
            var port = p.ToUpperInvariant();
            terminals.DropDownItems.Add(new ToolStripMenuItem($"Open {port} in its own window", null, (_, _) => TerminalForm.NewWindow(port)));
        }
        _menu.Items.Add(terminals);
        var screens = new ToolStripMenuItem("Screens");
        screens.DropDownItems.Add(new ToolStripMenuItem("Show the screen windows", null, (_, _) => ShowScreens()) { Font = new Font(_menu.Font, FontStyle.Bold) });
        screens.DropDownItems.Add(new ToolStripMenuItem("New screen window", null, (_, _) => ScreensForm.NewWindow()));
        screens.DropDownItems.Add(new ToolStripSeparator());
        try
        {
            foreach (var d in Vga.Devices())
                screens.DropDownItems.Add(new ToolStripMenuItem("Open " + d, null, (_, _) => ScreensForm.ShowDevice(d)));
        }
        catch (Exception e) { screens.DropDownItems.Add(new ToolStripMenuItem("capture devices: " + e.Message) { Enabled = false }); }
        _menu.Items.Add(screens);
        _menu.Items.Add(new ToolStripSeparator());

        var ports = new ToolStripMenuItem("Ports");
        var desc = Bench.PortDescriptions();
        foreach (var p in Bench.ComPorts().Concat(Bench.Lines.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ThenBy(p => p))
        {
            Bench.Lines.TryGetValue(p.ToUpperInvariant(), out var l);
            bool open = l?.IsOpen == true;
            ports.DropDownItems.Add(new ToolStripMenuItem($"{p}  {desc.GetValueOrDefault(p, "")}{(p.Equals(Bench.DefaultPort, StringComparison.OrdinalIgnoreCase) ? "  (default)" : "")}", null, (_, _) =>
            {
                var line = Bench.Line(p);
                try
                {
                    if (line.IsOpen) line.Close("tray");
                    else line.Open(null, "tray");
                }
                catch (Exception e) { _icon.ShowBalloonTip(5000, $"{line.Name} did not open", e.Message, ToolTipIcon.Warning); }
            }) { Checked = open });
        }
        if (ports.DropDownItems.Count == 0) ports.DropDownItems.Add(new ToolStripMenuItem("no COM ports") { Enabled = false });
        _menu.Items.Add(ports);

        var sessions = new ToolStripMenuItem("Sessions");
        lock (Bench.Clients)
            foreach (var c in Bench.Clients.Values.OrderBy(c => c.Seen))
                sessions.DropDownItems.Add(new ToolStripMenuItem($"{c.Label}  {(c.Doing != "" ? c.Doing : $"idle {(DateTime.UtcNow - c.Seen).TotalMinutes:0} min")}") { Enabled = false });
        if (sessions.DropDownItems.Count == 0) sessions.DropDownItems.Add(new ToolStripMenuItem("none connected") { Enabled = false });
        _menu.Items.Add(sessions);

        var recordings = new ToolStripMenuItem("Recordings");
        List<Recording> active;
        lock (Recordings.Active) active = Recordings.Active.ToList();
        foreach (var r in active)
        {
            var rec = r;
            recordings.DropDownItems.Add(new ToolStripMenuItem("Stop " + rec.Describe(), null, (_, _) => Recordings.Stop(rec)));
        }
        if (active.Count == 0) recordings.DropDownItems.Add(new ToolStripMenuItem("nothing is being recorded") { Enabled = false });
        else recordings.DropDownItems.Add(new ToolStripMenuItem("Stop all", null, (_, _) => Recordings.StopAll()));
        recordings.DropDownItems.Add(new ToolStripSeparator());
        recordings.DropDownItems.Add(new ToolStripMenuItem("Open the recordings folder", null, (_, _) =>
        {
            Directory.CreateDirectory(Recordings.Folder);
            TerminalForm.OpenPath(Recordings.Folder);
        }));
        _menu.Items.Add(recordings);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            Bench.Config.StartWithWindows = !Bench.Config.StartWithWindows;
            Bench.Save();
            Autostart(Bench.Config.StartWithWindows);
        }) { Checked = Bench.Config.StartWithWindows });
        _menu.Items.Add(new ToolStripMenuItem("Open the bench folder", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Bench.Dir) { UseShellExecute = true }); } catch { }
        }));
        _menu.Items.Add(new ToolStripMenuItem($"Listening on 127.0.0.1:{Mcp.Port}{(Mcp.Error != "" ? " - " + Mcp.Error : "")}") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Autostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (on) k.SetValue("CorsacBench", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("CorsacBench", false);
        }
        catch { }
    }

    void Exit()
    {
        _icon.Visible = false;
        Bench.Save();
        Bench.Exiting = true;
        lock (Bench.Lines)
            foreach (var l in Bench.Lines.Values)
                if (l.IsOpen) { try { l.Close("exit"); } catch { } }
        ScreensForm.CloseAllForExit();
        TerminalForm.CloseAllForExit();
        Recordings.CloseForExit();
        Vga.Stop();
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
