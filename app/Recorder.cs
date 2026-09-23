// Recording serial ports to files: any number at once, each of one port or
// several, as raw bytes or as text. Kept in bench.json while they run, so a
// bench restarted in the middle of one carries on appending to the same file.
//
// These are separate from the always-on serial-<PORT>.log: that one is the
// bench's own record of everything, this is a file somebody asked for, where
// they asked for it, in the form they wanted.

using System.Text;

namespace CorsacBench;

public enum RecordFormat
{
    /// The bytes exactly as they arrived: escape sequences, CRs and all.
    Raw,
    /// Readable text: escape sequences and carriage returns taken out.
    Text,
    /// Readable text with each line's time in front of it.
    Timestamped,
}

public sealed class RecordingConfig
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public List<string> Ports { get; set; } = new();
    public RecordFormat Format { get; set; } = RecordFormat.Timestamped;
    /// What was sent to the port as well, marked with who sent it (text formats only).
    public bool IncludeSent { get; set; } = true;
    public string StartedBy { get; set; } = "";
    public DateTime Started { get; set; }
}

public sealed class Recording
{
    public readonly RecordingConfig C;
    readonly FileStream _file;
    readonly Dictionary<string, TextState> _state = new();
    public long Bytes;
    public string Error = "";

    /// Per port: where its text is, so an escape sequence or a line split
    /// across two reads comes out whole.
    sealed class TextState
    {
        public int Escape;          // 0 none, 1 after ESC, 2 in CSI, 3 in OSC/DCS, 4 OSC saw ESC
        public bool MidLine;
    }

    public Recording(RecordingConfig c, bool append)
    {
        C = c;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(c.Path)!);
        _file = new FileStream(c.Path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        foreach (var p in c.Ports) _state[p] = new TextState();
        if (c.Format != RecordFormat.Raw)
            WriteLine($"---- recording {string.Join(", ", c.Ports)} started {DateTime.Now:yyyy-MM-dd HH:mm:ss}{(c.StartedBy != "" ? " by " + c.StartedBy : "")} ----");
    }

    bool Several => C.Ports.Count > 1;

    public void Received(Line line, byte[] data)
    {
        lock (this)
        {
            try
            {
                if (C.Format == RecordFormat.Raw) { _file.Write(data); Bytes += data.Length; }
                else
                {
                    FlushStaleSent();
                    Text(line.Name, data);
                }
                _file.Flush();
            }
            catch (Exception e) { Error = e.Message; }
        }
    }

    /// What has been sent and not yet written: keys typed at the window
    /// arrive one at a time, and a line per key is no record of anything.
    sealed class Pending
    {
        public readonly StringBuilder Text = new();
        public string Who = "";
        public DateTime Last;
    }
    readonly Dictionary<string, Pending> _pending = new();

    public void Sent(Line line, byte[] data, string who)
    {
        if (!C.IncludeSent || C.Format == RecordFormat.Raw) return;
        lock (this)
        {
            try
            {
                if (!_pending.TryGetValue(line.Name, out var p)) _pending[line.Name] = p = new Pending();
                if (p.Text.Length > 0 && p.Who != who) FlushSent(line.Name);
                p.Who = who;
                p.Last = DateTime.UtcNow;
                bool end = false;
                foreach (byte b in data)
                {
                    p.Text.Append(b == '\r' ? "\\r" : b == '\n' ? "\\n" : b == 27 ? "\\e" : b == 0x7F ? "^?" : b < 32 ? $"^{(char)(b + 64)}" : ((char)b).ToString());
                    if (b == '\r' || b == '\n') end = true;
                }
                // A LINE'S WORTH GOES OUT when Return is sent, when it grows
                // long, or when it has sat for two seconds (checked as data
                // arrives): whole commands, not keystrokes.
                if (end || p.Text.Length > 200) FlushSent(line.Name);
                _file.Flush();
            }
            catch (Exception e) { Error = e.Message; }
        }
    }

    /// WHAT WAS SENT GETS A LINE OF ITS OWN, marked with who sent it, so the
    /// file reads as a conversation rather than an echo tangled into the reply.
    void FlushSent(string port)
    {
        if (!_pending.TryGetValue(port, out var p) || p.Text.Length == 0) return;
        var st = _state[port];
        if (st.MidLine) { WriteRaw("\n"); st.MidLine = false; }
        EndOthers(port);
        WriteRaw(Prefix(port) + $"<<< {p.Who}: {p.Text}\n");
        p.Text.Clear();
    }

    void FlushStaleSent()
    {
        foreach (var kv in _pending)
            if (kv.Value.Text.Length > 0 && DateTime.UtcNow - kv.Value.Last > TimeSpan.FromSeconds(2)) FlushSent(kv.Key);
    }

    string Prefix(string port) =>
        (C.Format == RecordFormat.Timestamped ? $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] " : "") + (Several ? $"[{port}] " : "");

    void Text(string port, byte[] data)
    {
        var st = _state[port];
        var sb = new StringBuilder();
        foreach (byte b in data)
        {
            switch (st.Escape)
            {
                case 1:
                    st.Escape = b == '[' ? 2 : b == ']' || b == 'P' || b == '_' || b == '^' ? 3 : b == '(' || b == ')' || b == '#' ? 5 : 0;
                    continue;
                case 2:
                    if (b >= 0x40 && b <= 0x7E) st.Escape = 0;
                    continue;
                case 3:
                    if (b == 7) st.Escape = 0;
                    else if (b == 27) st.Escape = 4;
                    continue;
                case 4:
                    st.Escape = b == '\\' ? 0 : 3;
                    continue;
                case 5:
                    st.Escape = 0;          // the charset or line-size letter
                    continue;
            }
            if (b == 27) { st.Escape = 1; continue; }
            if (b == '\r' || b == 0 || b == 7 || b == 0x7F) continue;
            if (b == '\b') { if (sb.Length > 0 && sb[^1] != '\n') sb.Length--; continue; }
            if (b < 32 && b != '\n' && b != '\t') continue;
            if (!st.MidLine)
            {
                if (sb.Length == 0) EndOthers(port);
                sb.Append(Prefix(port));
                st.MidLine = true;
            }
            sb.Append((char)b);
            if (b == '\n') st.MidLine = false;
        }
        WriteRaw(sb.ToString());
    }

    /// SEVERAL PORTS IN ONE FILE take a line each: another port's line left
    /// unfinished -- a prompt, say -- is ended before this one starts, and
    /// picks up under its own mark when it goes on.
    void EndOthers(string port)
    {
        if (!Several) return;
        foreach (var kv in _state)
            if (kv.Key != port && kv.Value.MidLine) { WriteRaw("\n"); kv.Value.MidLine = false; }
    }

    void WriteLine(string s) => WriteRaw(s + "\n");

    void WriteRaw(string s)
    {
        var bytes = Encoding.Latin1.GetBytes(s);
        _file.Write(bytes);
        Bytes += bytes.Length;
    }

    public void Close()
    {
        lock (this)
        {
            try
            {
                if (C.Format != RecordFormat.Raw)
                {
                    foreach (var port in _pending.Keys.ToList()) FlushSent(port);
                    foreach (var st in _state.Values) if (st.MidLine) { WriteRaw("\n"); st.MidLine = false; }
                    WriteLine($"---- recording stopped {DateTime.Now:yyyy-MM-dd HH:mm:ss} ----");
                }
                _file.Dispose();
            }
            catch { }
        }
    }

    public string Describe() =>
        $"{C.Id}  {string.Join("+", C.Ports)} -> {C.Path}  ({C.Format switch { RecordFormat.Raw => "raw", RecordFormat.Text => "text", _ => "timestamped" }}{(C.IncludeSent && C.Format != RecordFormat.Raw ? ", with what was sent" : "")}, {Bytes / 1024.0:0.#} KB{(Error != "" ? ", error: " + Error : "")})";
}

public static class Recordings
{
    public static readonly List<Recording> Active = new();
    public static event Action? Changed;
    static bool _hooked;

    public static string Folder => System.IO.Path.Combine(Bench.Dir, "recordings");

    /// Where a recording goes: a Windows path as it is, a WSL path (/home/...)
    /// through \\wsl.localhost, a bare name into the recordings folder, and
    /// nothing at all a name made from the ports and the time.
    public static string Resolve(string? path, IEnumerable<string> ports, RecordFormat format)
    {
        if (string.IsNullOrWhiteSpace(path))
            return System.IO.Path.Combine(Folder, $"{string.Join("+", ports)}-{DateTime.Now:yyyyMMdd-HHmmss}.{(format == RecordFormat.Raw ? "bin" : "log")}");
        path = path.Trim();
        if (path.StartsWith("/"))
            return $@"\\wsl.localhost\{Bench.WslDistro()}" + path.Replace('/', '\\');
        if (!System.IO.Path.IsPathRooted(path)) return System.IO.Path.Combine(Folder, path);
        return path;
    }

    static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        Bench.LineMade += l =>
        {
            l.Received += (line, data) => { foreach (var r in For(line.Name)) r.Received(line, data); };
            l.Sent += (line, data, who) => { foreach (var r in For(line.Name)) r.Sent(line, data, who); };
        };
        lock (Bench.Lines)
            foreach (var l in Bench.Lines.Values)
            {
                l.Received += (line, data) => { foreach (var r in For(line.Name)) r.Received(line, data); };
                l.Sent += (line, data, who) => { foreach (var r in For(line.Name)) r.Sent(line, data, who); };
            }
    }

    static List<Recording> For(string port)
    {
        lock (Active) return Active.Where(r => r.C.Ports.Contains(port)).ToList();
    }

    public static Recording Start(IEnumerable<string> ports, string? path, RecordFormat format, bool includeSent, bool append, string who)
    {
        Hook();
        var list = ports.Select(p => p.Trim().ToUpperInvariant()).Where(p => p != "").Distinct().ToList();
        if (list.Count == 0) throw new ArgumentException("no port to record");
        foreach (var p in list) Bench.Line(p);
        var c = new RecordingConfig
        {
            Id = Guid.NewGuid().ToString("N")[..6],
            Path = Resolve(path, list, format),
            Ports = list,
            Format = format,
            IncludeSent = includeSent,
            StartedBy = who,
            Started = DateTime.Now,
        };
        lock (Active)
            if (Active.Any(r => string.Equals(r.C.Path, c.Path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{c.Path} is already being recorded to");
        var rec = new Recording(c, append);
        lock (Active) Active.Add(rec);
        Save();
        Changed?.Invoke();
        return rec;
    }

    /// Stops the recordings whose id or path is `which`, or every one on a port.
    public static List<Recording> Stop(string which)
    {
        List<Recording> gone;
        lock (Active)
        {
            gone = Active.Where(r => r.C.Id == which || string.Equals(r.C.Path, which, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(r.C.Path, Resolve(which, r.C.Ports, r.C.Format), StringComparison.OrdinalIgnoreCase)
                                     || (r.C.Ports.Count == 1 && r.C.Ports[0] == which.ToUpperInvariant())).ToList();
            foreach (var r in gone) Active.Remove(r);
        }
        foreach (var r in gone) r.Close();
        Save();
        Changed?.Invoke();
        return gone;
    }

    public static void Stop(Recording r)
    {
        lock (Active) Active.Remove(r);
        r.Close();
        Save();
        Changed?.Invoke();
    }

    public static int StopAll()
    {
        List<Recording> all;
        lock (Active) { all = Active.ToList(); Active.Clear(); }
        foreach (var r in all) r.Close();
        Save();
        Changed?.Invoke();
        return all.Count;
    }

    /// At exit: the files are closed and the list is kept, so the next start
    /// appends to them.
    public static void CloseForExit()
    {
        lock (Active) foreach (var r in Active) r.Close();
    }

    static void Save()
    {
        lock (Active) Bench.Config.Recordings = Active.Select(r => r.C).ToList();
        Bench.Save();
    }

    /// The recordings that were running when the bench last stopped, appending.
    public static void Restore()
    {
        Hook();
        foreach (var c in Bench.Config.Recordings.ToList())
        {
            try
            {
                foreach (var p in c.Ports) Bench.Line(p);
                var rec = new Recording(c, true);
                lock (Active) Active.Add(rec);
            }
            catch (Exception e) { Program.Crashed(new IOException($"recording {c.Path} not resumed: {e.Message}"), false); }
        }
        Save();
    }
}
