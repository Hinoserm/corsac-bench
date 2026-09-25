// The bench's state and its tools: the ports, the clients, the capture
// device, and what each MCP tool does with them. One of these exists per
// Windows login; every assistant session talks to it over HTTP.

using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CorsacBench;

/// <summary>One connected assistant session. Keeps its own place in every port's stream.</summary>
public sealed class Client
{
    public readonly string Id;
    public string Name = "client";
    public DateTime Seen = DateTime.UtcNow;
    public string Doing = "";
    public readonly Dictionary<string, long> Cursors = new();

    public Client(string id) { Id = id; }
    public string Label => $"{Name} ({Id[..6]})";
}

public sealed class BenchConfig
{
    public string DefaultPort { get; set; } = "COM1";
    public int Baud { get; set; } = 115200;
    /// A part of the default capture device's name; empty is the first device.
    public string Vga { get; set; } = "";
    public int Listen { get; set; } = 7825;
    /// The Python the capture helper runs under; empty finds one (PATH, then Program Files).
    public string Python { get; set; } = "";
    public List<string> OpenAtStart { get; set; } = new();
    public string Font { get; set; } = "Cascadia Mono";
    public float FontSize { get; set; } = 10f;
    public int Cols { get; set; } = 80;
    public int Rows { get; set; } = 25;
    public bool StartWithWindows { get; set; } = true;
    public Dictionary<string, ScreenSettings> Screens { get; set; } = new();
    public List<ScreenWindowConfig> ScreenWindows { get; set; } = new();
    public List<TerminalWindowConfig> TerminalWindows { get; set; } = new();
    public List<RecordingConfig> Recordings { get; set; } = new();
    /// Which WSL distribution a recording path starting with / is in; empty is the default one.
    public string WslDistro { get; set; } = "";
    /// What serial_reset and the Reset button send. The default is the CORSAC
    /// kernel's: ESC ESC ESC RESET, taken from the serial interrupt.
    public string ResetSequence { get; set; } = "\u001b\u001b\u001bRESET";
    /// What serial_reset waits for after sending it: the machine's first words.
    public string ResetBanner { get; set; } = "CORSAC boot";
    /// The shell prompt serial_command waits for, at the start of a line.
    public string ShellPrompt { get; set; } = "# ";
    /// How much of each port's output the bench keeps in memory for
    /// serial_history and serial_find; the oldest goes once it is full.
    public int SerialHistoryMiB { get; set; } = 1024;
}

public static class Bench
{
    /// Where bench.json, the logs, the recordings and vga.png live: CORSAC_DIR,
    /// else C:\CORSAC\bench where that already exists, else the user's own
    /// local application data.
    public static string Dir = Environment.GetEnvironmentVariable("CORSAC_DIR")
        ?? (Directory.Exists(@"C:\CORSAC\bench") ? @"C:\CORSAC\bench"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "corsac-bench"));
    public static BenchConfig Config = new();
    static string ConfigPath => Path.Combine(Dir, "bench.json");

    public static byte[] Magic => Encoding.Latin1.GetBytes(Config.ResetSequence);
    static byte[] Prompt => Encoding.Latin1.GetBytes("\n" + Config.ShellPrompt);

    /// The Python to run the capture helper with: the configured one, else
    /// the first real python.exe on PATH (not the Store's stub), else the
    /// newest under Program Files.
    public static string PythonPath()
    {
        if (Config.Python != "" && File.Exists(Config.Python)) return Config.Python;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (dir == "" || dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = Path.Combine(dir.Trim(), "python.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var found = Directory.Exists(programs) ? Directory.GetDirectories(programs, "Python3*").OrderByDescending(d => d.Length).ThenByDescending(d => d).Select(d => Path.Combine(d, "python.exe")).FirstOrDefault(File.Exists) : null;
        return found ?? "python.exe";
    }

    /// The WSL distribution /-paths are in: the configured one, else Windows' default.
    public static string WslDistro()
    {
        if (Config.WslDistro != "") return Config.WslDistro;
        try
        {
            using var lxss = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxss?.GetValue("DefaultDistribution") is string guid)
                using (var d = lxss.OpenSubKey(guid))
                    if (d?.GetValue("DistributionName") is string name) return name;
        }
        catch { }
        return "Ubuntu";
    }

    public static readonly Dictionary<string, Line> Lines = new();
    public static readonly Dictionary<string, Client> Clients = new();
    public static string DefaultPort = "COM1";
    public static string DefaultVga = "";

    public static event Action? LinesChanged;
    /// <summary>Raised once for each Line when it is first made.</summary>
    public static event Action<Line>? LineMade;
    public static event Action? ClientsChanged;

    public static void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                Config = JsonSerializer.Deserialize<BenchConfig>(File.ReadAllText(ConfigPath)) ?? new();
        }
        catch { }
        if (Environment.GetEnvironmentVariable("CORSAC_COM") is { Length: > 0 } com) Config.DefaultPort = com;
        if (Environment.GetEnvironmentVariable("CORSAC_VGA") is { Length: > 0 } vga) Config.Vga = vga;
        DefaultPort = Config.DefaultPort.ToUpperInvariant();
        DefaultVga = Config.Vga;
        Save();
    }

    /// <summary>Set while the bench shuts down: closing its ports then must not be remembered as the ports being closed.</summary>
    public static bool Exiting;

    public static void Save()
    {
        if (Exiting) return;
        try
        {
            Directory.CreateDirectory(Dir);
            // The ports open now are the ones opened at the next start -- none, if
            // they were all closed. Before the start has opened them, the list
            // read from the file stands.
            if (_started)
                lock (Lines) Config.OpenAtStart = Lines.Values.Where(l => l.IsOpen).Select(l => l.Name).ToList();
            Config.DefaultPort = DefaultPort;
            Config.Vga = DefaultVga;
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    static bool _started;

    public static void OpenAtStart()
    {
        _started = true;
        foreach (var name in Config.OpenAtStart.ToList())
        {
            var l = Line(name);
            try { l.Open(new LineSettings { Baud = Config.Baud }, "bench"); }
            catch (Exception e) { l.Error = "did not open at start: " + e.Message; }
        }
    }

    public static Line Line(string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? DefaultPort : name.Trim().ToUpperInvariant();
        lock (Lines)
        {
            if (!Lines.TryGetValue(name, out var l))
            {
                l = new Line(name);
                l.Changed += _ => { Save(); LinesChanged?.Invoke(); };
                Lines[name] = l;
                LineMade?.Invoke(l);
                LinesChanged?.Invoke();
            }
            return l;
        }
    }

    public static Client Client(string? id, string? name = null)
    {
        lock (Clients)
        {
            id ??= Guid.NewGuid().ToString("N");
            bool fresh = !Clients.TryGetValue(id, out var c);
            if (fresh)
            {
                c = new Client(id);
                Clients[id] = c;
            }
            if (name != null) c!.Name = name;
            c!.Seen = DateTime.UtcNow;
            // A client silent for an hour is gone; a bridge that exits says so itself.
            foreach (var old in Clients.Values.Where(x => x.Doing == "" && DateTime.UtcNow - x.Seen > TimeSpan.FromHours(1)).ToList())
                Clients.Remove(old.Id);
            if (fresh || name != null) ClientsChanged?.Invoke();
            return c;
        }
    }

    public static void Forget(string id)
    {
        lock (Clients) Clients.Remove(id);
        ClientsChanged?.Invoke();
    }

    /// <summary>A client's place in a port's stream; a client new to the port starts at what arrives next.</summary>
    static long CursorOf(Client c, Line l)
    {
        lock (c.Cursors)
        {
            if (!c.Cursors.TryGetValue(l.Name, out var at)) c.Cursors[l.Name] = at = l.End;
            return at;
        }
    }

    static void SetCursor(Client c, Line l, long at)
    {
        lock (c.Cursors) c.Cursors[l.Name] = at;
    }

    public static string Text(byte[] b) => Encoding.Latin1.GetString(b).Replace("\r", "");

    static string? Str(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue(out string? s) ? s : a[k]?.ToString();
    static double Num(JsonObject a, string k, double dflt) => a[k] is JsonValue v && v.TryGetValue(out double d) ? d : double.TryParse(Str(a, k), out d) ? d : dflt;
    static bool? Bool(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    static LineSettings SettingsOf(JsonObject a, LineSettings from)
    {
        var s = from.Clone();
        if (a["baud"] != null) s.Baud = (int)Num(a, "baud", s.Baud);
        if (a["bytesize"] != null) s.ByteSize = (int)Num(a, "bytesize", s.ByteSize);
        if (Str(a, "parity") is { Length: > 0 } p)
            s.Parity = char.ToUpperInvariant(p[0]) switch
            {
                'N' => Parity.None, 'E' => Parity.Even, 'O' => Parity.Odd, 'M' => Parity.Mark, 'S' => Parity.Space,
                _ => throw new ArgumentException("parity must be N, E, O, M or S"),
            };
        if (a["stopbits"] != null)
            s.StopBits = Num(a, "stopbits", 1) switch { 1.5 => StopBits.OnePointFive, 2 => StopBits.Two, _ => StopBits.One };
        if (Bool(a, "rtscts") is bool r) s.RtsCts = r;
        if (Bool(a, "xonxoff") is bool x) s.XonXoff = x;
        return s;
    }

    public static string[] ComPorts() => SerialPort.GetPortNames().Distinct().OrderBy(p => p.Length).ThenBy(p => p).ToArray();

    /// <summary>What each port is, from the registry's device map and the device's friendly name.</summary>
    public static Dictionary<string, string> PortDescriptions()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var map = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (map != null)
                foreach (var n in map.GetValueNames())
                    if (map.GetValue(n) is string port) d[port] = n.Replace(@"\Device\", "");
        }
        catch { }
        return d;
    }

    // ---- the tools ----

    static JsonObject Ok(string s) => new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = s }) };

    public static JsonObject Call(Client c, string name, JsonObject a)
    {
        c.Doing = name;
        ClientsChanged?.Invoke();
        try { return CallCore(c, name, a); }
        finally { c.Doing = ""; c.Seen = DateTime.UtcNow; ClientsChanged?.Invoke(); }
    }

    static JsonObject CallCore(Client c, string name, JsonObject a)
    {
        string who = c.Label;
        switch (name)
        {
            case "serial_ports":
            {
                var desc = PortDescriptions();
                var rows = ComPorts().Select(p =>
                {
                    Line? l; lock (Lines) Lines.TryGetValue(p.ToUpperInvariant(), out l);
                    return $"{p} | {desc.GetValueOrDefault(p, "")} | {(l != null ? l.Describe() : "not opened here")}{(p.Equals(DefaultPort, StringComparison.OrdinalIgnoreCase) ? " | DEFAULT" : "")}";
                });
                return Ok(string.Join("\n", rows).NullIfEmpty() ?? "no COM ports");
            }
            case "serial_open":
            {
                var l = Line(Str(a, "port"));
                l.Open(SettingsOf(a, l.Settings), who);
                if (Bool(a, "default") ?? true) DefaultPort = l.Name;
                Save();
                return Ok($"opened {l.Describe()}{(DefaultPort == l.Name ? " (default)" : "")}; every client of the bench shares it");
            }
            case "serial_close":
            {
                var l = Line(Str(a, "port"));
                l.Close(who);
                return Ok($"closed {l.Name} for every client of the bench");
            }
            case "serial_configure":
            {
                var l = Line(Str(a, "port"));
                l.Configure(SettingsOf(a, l.Settings), who);
                return Ok("now " + l.Describe());
            }
            case "serial_default":
                DefaultPort = (Str(a, "port") ?? DefaultPort).ToUpperInvariant();
                Save();
                LinesChanged?.Invoke();
                return Ok("default port is " + DefaultPort + " (for every client of the bench)");
            case "serial_status":
            {
                var l = Line(Str(a, "port"));
                long at = CursorOf(c, l), end = l.End;
                string clients;
                lock (Clients) clients = string.Join(", ", Clients.Values.Select(x => x.Label + (x.Doing != "" ? " [" + x.Doing + "]" : "")));
                return Ok($"{l.Describe()} received={end} unread={end - at} error={l.Error} log={l.LogPath}" +
                          $"{(DefaultPort == l.Name ? " (default)" : "")}{(l.OpenedBy != "" ? " opened-by=" + l.OpenedBy : "")}" +
                          $"{(l.ConversationHolder != "" ? " command-running-for=" + l.ConversationHolder : "")}\nclients: {clients}");
            }
            case "serial_read":
            {
                var l = Line(Str(a, "port"));
                Thread.Sleep(TimeSpan.FromSeconds(Num(a, "seconds", 1)));
                long at = CursorOf(c, l);
                var got = l.Take(ref at);
                SetCursor(c, l, at);
                return Ok(Text(got));
            }
            case "serial_history":
            {
                // A page of everything the port has sent, by position.
                var l = Line(Str(a, "port"));
                int bytes = (int)Math.Clamp(Num(a, "bytes", 16384), 1, CorsacBench.Line.Most);
                long start = l.Start, end = l.End;
                long from = a["from"] == null ? end - bytes : (long)Num(a, "from", 0);
                if (from < 0) from = end + from;
                long asked = from;
                from = Math.Clamp(from, start, end);
                long to = Math.Min(end, from + bytes);
                var page = l.Read(from, to);
                string When(long at) => l.When(at) is { } w ? w.ToString("yyyy-MM-dd HH:mm:ss") : "?";
                var head = $"[{l.Name}: bytes {from} to {to} of {start} to {end} held (keeps the newest {l.Capacity >> 20} MiB)" +
                           (to > from ? $"; arrived {When(from)} to {When(to - 1)}" : "") +
                           (from > start ? $"; earlier page: from={Math.Max(start, from - bytes)}" : "; this is the oldest held") +
                           (to < end ? $"; later page: from={to}" : "; this is the newest") + "]\n" +
                           (asked < start ? $"[bytes {Math.Max(0, asked)} to {start} are no longer held: the oldest went when the history passed {l.Capacity >> 20} MiB]\n" : "");
                return Ok(head + Text(page));
            }
            case "serial_find":
            {
                var l = Line(Str(a, "port"));
                string needle = Str(a, "text") is { Length: > 0 } t ? t : throw new ArgumentException("text is required");
                bool backwards = Bool(a, "backwards") ?? true;
                int count = (int)Math.Clamp(Num(a, "count", 20), 1, 200);
                long start = l.Start, end = l.End;
                long at = a["from"] == null ? (backwards ? end : start) : (long)Num(a, "from", 0);
                if (at < 0) at = end + at;
                at = Math.Clamp(at, start, end);
                var bytes = Encoding.Latin1.GetBytes(needle);
                var sb = new StringBuilder();
                int found = 0;
                long next = at;
                while (found < count)
                {
                    long hit = backwards ? l.Find(start, next, bytes, true) : l.Find(next, end, bytes, false);
                    if (hit < 0) { next = -1; break; }
                    found++;
                    // The line it is on, cut to 300 characters either side.
                    long ls = hit, le = hit + bytes.Length;
                    var before = l.Read(Math.Max(start, hit - 300), hit);
                    int nl = Array.LastIndexOf(before, (byte)'\n');
                    ls = hit - before.Length + nl + 1;
                    var after = l.Read(le, Math.Min(end, le + 300));
                    int nr = Array.IndexOf(after, (byte)'\n');
                    le += nr < 0 ? after.Length : nr;
                    sb.Append($"{hit}  {(l.When(hit) is { } w ? w.ToString("yyyy-MM-dd HH:mm:ss") : "?")}  {Text(l.Read(ls, le)).TrimEnd()}\n");
                    next = backwards ? hit : hit + bytes.Length;
                }
                var head = $"[{l.Name}: {found} match{(found == 1 ? "" : "es")} for \"{needle}\", {(backwards ? "newest first" : "oldest first")}, in {start} to {end}" +
                           (next >= 0 ? $"; more: from={next}" : "; no more") + "]\n";
                return Ok(head + sb.ToString());
            }
            case "serial_send":
            {
                var l = Line(Str(a, "port"));
                var data = Encoding.Latin1.GetBytes((Str(a, "text") ?? "") + ((Bool(a, "newline") ?? true) ? "\n" : ""));
                l.Send(data, who);
                return Ok($"sent {data.Length} bytes on {l.Name}");
            }
            case "serial_wait":
            {
                var l = Line(Str(a, "port"));
                var needle = Str(a, "text") ?? throw new ArgumentException("text is required");
                long at = CursorOf(c, l);
                var (found, got) = l.Wait(ref at, Encoding.Latin1.GetBytes(needle), TimeSpan.FromSeconds(Num(a, "seconds", 60)));
                SetCursor(c, l, at);
                return Ok(Text(got) + (found ? "" : $"\n[timed out waiting for \"{needle}\"]"));
            }
            case "serial_command":
                return Converse(c, a, Num(a, "seconds", 120), l =>
                {
                    long at = l.End;
                    l.Send(Encoding.Latin1.GetBytes((Str(a, "command") ?? "") + "\n"), who);
                    var (found, got) = l.Wait(ref at, Prompt, TimeSpan.FromSeconds(Num(a, "seconds", 120)));
                    SetCursor(c, l, at);
                    return Text(got) + (found ? "" : "\n[no prompt came back within the time]");
                });
            case "serial_login":
                return Converse(c, a, Num(a, "seconds", 180), l =>
                {
                    long at = CursorOf(c, l);
                    var (found, got) = l.Wait(ref at, Encoding.ASCII.GetBytes("login:"), TimeSpan.FromSeconds(Num(a, "seconds", 180)));
                    if (!found) { SetCursor(c, l, at); return Text(got) + "\n[no login prompt within the time]"; }
                    Thread.Sleep(500);
                    l.Send(Encoding.Latin1.GetBytes((Str(a, "user") ?? "root") + "\n"), who);
                    var (found2, got2) = l.Wait(ref at, Encoding.Latin1.GetBytes(Config.ShellPrompt), TimeSpan.FromSeconds(90));
                    SetCursor(c, l, at);
                    return Text(got.Concat(got2).ToArray()) + (found2 ? "" : "\n[no shell prompt after logging in]");
                });
            case "serial_reset":
                return Converse(c, a, 30, l =>
                {
                    long at = l.End;
                    l.Send(Magic, who);
                    var (found, got) = l.Wait(ref at, Encoding.Latin1.GetBytes(Config.ResetBanner), TimeSpan.FromSeconds(Num(a, "seconds", 30)));
                    SetCursor(c, l, at);
                    return (found ? "machine reset; loader is up\n" : "no loader banner seen after the reset sequence\n") + Text(got);
                });
            case "serial_tail":
                return Ok(Text(Line(Str(a, "port")).Tail((int)Num(a, "chars", 4000))));
            case "serial_screen":
            {
                var l = Line(Str(a, "port"));
                var sb = new StringBuilder();
                lock (l.Term)
                {
                    int from = Math.Max(0, l.Term.Scrollback.Count - (int)Num(a, "history", 0));
                    for (int i = from; i < l.Term.TotalLines; i++) sb.Append(l.Term.LineText(i)).Append('\n');
                    sb.Append($"[{l.Name} terminal {l.Term.Cols}x{l.Term.Rows}, cursor at row {l.Term.CursorY + 1} column {l.Term.CursorX + 1}{(l.Term.AltScreen ? ", full-screen program" : "")}]");
                }
                return Ok(sb.ToString());
            }
            case "serial_record_start":
            {
                var names = (a["ports"] is JsonArray arr ? arr.Select(n => n!.ToString()) : (Str(a, "ports") ?? Str(a, "port") ?? DefaultPort).Split(','))
                    .Select(p => p.Trim()).Where(p => p != "").ToList();
                var format = (Str(a, "format") ?? "timestamped").Trim().ToLowerInvariant() switch
                {
                    "raw" => RecordFormat.Raw,
                    "text" => RecordFormat.Text,
                    "timestamped" or "" => RecordFormat.Timestamped,
                    var other => throw new ArgumentException($"format is raw, text or timestamped, not \"{other}\""),
                };
                var rec = Recordings.Start(names, Str(a, "path"), format, Bool(a, "include_sent") ?? true, Bool(a, "append") ?? true, who);
                return Ok("recording " + rec.Describe());
            }
            case "serial_record_stop":
            {
                List<Recording> gone;
                if (Bool(a, "all") == true) return Ok($"stopped {Recordings.StopAll()} recording(s)");
                var which = Str(a, "id") ?? Str(a, "path") ?? Str(a, "port") ?? throw new ArgumentException("say which: id, path, port, or all");
                gone = Recordings.Stop(which);
                return Ok(gone.Count == 0 ? $"no recording matches \"{which}\"" : "stopped:\n" + string.Join("\n", gone.Select(r => r.Describe())));
            }
            case "serial_record_list":
            {
                lock (Recordings.Active)
                    return Ok(Recordings.Active.Count == 0 ? "nothing is being recorded" : string.Join("\n", Recordings.Active.Select(r => r.Describe())));
            }
            case "bench_clients":
            {
                lock (Clients)
                    return Ok(string.Join("\n", Clients.Values.OrderBy(x => x.Seen).Select(x =>
                        $"{x.Label}{(x.Id == c.Id ? " (you)" : "")} last seen {(DateTime.UtcNow - x.Seen).TotalSeconds:0}s ago{(x.Doing != "" ? " doing " + x.Doing : "")}")));
            }
            case "vga_devices":
                return Ok(string.Join("\n", Vga.Devices().Select((d, i) => $"{i}: {d}{(d.Contains(DefaultVga, StringComparison.OrdinalIgnoreCase) ? "  (default)" : "")}")));
            case "vga_select":
                DefaultVga = Vga.Resolve(Str(a, "device") ?? throw new ArgumentException("device is required"));
                Save();
                return Ok("default capture device is " + DefaultVga);
            case "vga_capture_series":
            {
                var device = Vga.Resolve(Str(a, "device"));
                int count = (int)Math.Clamp(Num(a, "count", 8), 1, 64);
                double seconds = Math.Clamp(Num(a, "seconds", 10), 0, 3600);
                c.Doing = $"capturing {count} frames over {seconds:0.#} s";
                // Runs on this call's own thread and holds no lock while it
                // waits, so series run side by side with each other and every tool.
                try { return Series.Capture(device, count, seconds, Series.NewPath(Dir)); }
                finally { c.Doing = ""; }
            }
            case "vga_capture":
            {
                var device = Vga.Resolve(Str(a, "device"));
                var path = Path.Combine(Dir, "vga.png");
                using var ms = new MemoryStream();
                string text;
                var native = VideoSource.Use(device);
                if (native != null)
                {
                    // The card's own path: the next complete frame (a running
                    // view's latest if it is fresh), kept open a little while in
                    // case another capture follows.
                    try
                    {
                        var vf = native.Latest ?? native.Next(native.Seq, 3000) ?? throw new InvalidOperationException(native.Error != "" ? native.Error : "no frame from " + device);
                        using var b = vf.ToBitmap();
                        b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        text = $"{device}, {vf.Width}x{vf.Height} (signal {native.NativeW}x{native.NativeH}, {native.SignalHz:0.##} Hz" +
                               (native.TimingText != "" ? ", " + native.TimingText : "") +
                               (native.AspectX > 0 && native.AspectY > 0 ? $", aspect {native.AspectX}:{native.AspectY}" : "") + $"), saved to {path}";
                    }
                    finally { VideoSource.Release(native, 10000); }
                }
                else
                {
                    using var f = Vga.Frame(device, 0);
                    f.Picture.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    text = $"{device}, {f.Picture.Width}x{f.Picture.Height} (signal {f.NativeW}x{f.NativeH}), saved to {path}";
                }
                File.WriteAllBytes(path, ms.ToArray());
                return new JsonObject
                {
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = text },
                        new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(ms.ToArray()), ["mimeType"] = "image/png" }),
                };
            }
        }
        throw new ArgumentException("unknown tool " + name);
    }

    /// <summary>Runs a send-and-wait exchange with the port to itself, so another client's command cannot land in the middle.</summary>
    static JsonObject Converse(Client c, JsonObject a, double seconds, Func<Line, string> body)
    {
        var l = Line(Str(a, "port"));
        if (!l.Conversation.Wait(TimeSpan.FromSeconds(Math.Max(seconds, 1))))
            return Ok($"[{l.Name} is busy: {l.ConversationHolder} is running a command on it]");
        l.ConversationHolder = c.Label;
        try { return Ok(body(l)); }
        finally { l.ConversationHolder = ""; l.Conversation.Release(); }
    }

    static string? NullIfEmpty(this string s) => s.Length == 0 ? null : s;
}
