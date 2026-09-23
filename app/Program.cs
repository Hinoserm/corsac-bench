// CorsacBench.exe: the real machine's serial ports and VGA capture, owned by
// one process per Windows login and shared over MCP with every assistant
// session, with a terminal and a screen viewer for the person at the bench.
//
//   CorsacBench.exe            start (or bring forward the one already running)
//   CorsacBench.exe --screens  the same, opening the screen viewer

using System.Windows.Forms;

namespace CorsacBench;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool screens = args.Contains("--screens");
        using var only = new Mutex(true, @"Local\CorsacBench", out bool first);
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, screens ? @"Local\CorsacBench.Screens" : @"Local\CorsacBench.Show");
        if (!first)
        {
            wake.Set();
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Crashed(e.Exception, false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crashed(e.ExceptionObject as Exception, true);
        Bench.Load();
        try { Mcp.Start(Bench.Config.Listen); }
        catch (Exception e) { Mcp.Port = Bench.Config.Listen; Mcp.Error = $"could not listen on 127.0.0.1:{Bench.Config.Listen}: {e.Message}"; }
        Bench.OpenAtStart();
        TrayIcon.Autostart(Bench.Config.StartWithWindows);

        var tray = new TrayIcon();
        Watch(@"Local\CorsacBench.Show", TrayIcon.ShowTerminal);
        Watch(@"Local\CorsacBench.Screens", TrayIcon.ShowScreens);
        if (screens) TrayIcon.ShowScreens();
        Application.Run(tray);
        Bench.Save();
    }

    /// <summary>
    /// Every unhandled exception is written to crash.log. One on the UI thread
    /// is survived; one on another thread ends the program, and the log says why.
    /// </summary>
    static void Crashed(Exception? e, bool fatal)
    {
        try
        {
            Directory.CreateDirectory(Bench.Dir);
            File.AppendAllText(Path.Combine(Bench.Dir, "crash.log"),
                $"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} {(fatal ? "fatal" : "survived")}\n{e}\n");
        }
        catch { }
    }

    /// <summary>A second start of the program sets the event; this one answers by showing a window.</summary>
    static void Watch(string name, Action show)
    {
        var e = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        new Thread(() => { while (e.WaitOne()) TrayIcon.Post(show); }) { IsBackground = true, Name = name }.Start();
    }
}
