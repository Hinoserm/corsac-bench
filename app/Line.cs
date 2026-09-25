// One serial port, shared by every client of the bench and by the window.
//
// A thread reads the port from the moment it is opened into a buffer, the
// port's log and its terminal. Each client keeps its own place in the buffer,
// so what one reads another still gets; offsets are absolute, counted from
// the port's first byte, and survive the buffer dropping its oldest part.

using System.IO.Ports;
using System.Text;

namespace CorsacBench;

public sealed class LineSettings
{
    public int Baud = 115200;
    public int ByteSize = 8;
    public Parity Parity = Parity.None;
    public StopBits StopBits = StopBits.One;
    public bool RtsCts, XonXoff;

    public LineSettings Clone() => (LineSettings)MemberwiseClone();

    public override string ToString()
    {
        char p = Parity switch { Parity.Even => 'E', Parity.Odd => 'O', Parity.Mark => 'M', Parity.Space => 'S', _ => 'N' };
        string s = StopBits switch { StopBits.OnePointFive => "1.5", StopBits.Two => "2", _ => "1" };
        return $"{Baud} {ByteSize}{p}{s}{(RtsCts ? " rtscts" : "")}{(XonXoff ? " xonxoff" : "")}";
    }
}

public sealed class Line
{
    public readonly string Name;
    public LineSettings Settings = new();
    public string Error = "";
    public string OpenedBy = "";
    public readonly Vt Term = new();
    /// <summary>
    /// The view whose window decides the terminal's size when the port is
    /// shown in more than one: the one last given the keyboard. Two views
    /// each fitting the one screen to their own window would fight.
    /// </summary>
    public object? SizeOwner;

    /// <summary>Held by whoever is running a command, so two clients' commands do not interleave.</summary>
    public readonly SemaphoreSlim Conversation = new(1, 1);
    public string ConversationHolder = "";

    readonly object _gate = new();
    /// Everything received, up to SerialHistoryMiB; see History.
    readonly History _history = new((long)Math.Max(16, Bench.Config.SerialHistoryMiB) << 20);
    /// The most one read or wait hands back; anything older is left for
    /// serial_history, with a note saying where.
    public const int Most = 1 << 20;
    SerialPort? _port;
    FileStream? _log;

    /// <summary>Raised on the reader thread with every chunk received.</summary>
    public event Action<Line, byte[]>? Received;
    /// <summary>Raised with every write, and who wrote it ("window", "terminal", or a session's label).</summary>
    public event Action<Line, byte[], string>? Sent;
    /// <summary>Raised when the port opens, closes or changes settings.</summary>
    public event Action<Line>? Changed;

    public Line(string name)
    {
        Name = name;
        Term.Reply = data => { try { Send(data, "terminal"); } catch { } };
    }

    public bool IsOpen => _port?.IsOpen == true;
    public long End { get { lock (_gate) return _history.End; } }
    /// The oldest position still held.
    public long Start { get { lock (_gate) return _history.Start; } }
    public long Capacity => _history.Capacity;
    public string LogPath => Path.Combine(Bench.Dir, $"serial-{Name}.log");

    public string Describe() => $"{Name} {Settings}{(IsOpen ? " open" : " closed")}";

    void Log(string s) => Log(Encoding.Latin1.GetBytes(s));

    void Log(byte[] data)
    {
        try
        {
            if (_log == null)
            {
                Directory.CreateDirectory(Bench.Dir);
                _log = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            }
            _log.Write(data);
            _log.Flush();
        }
        catch { }
    }

    public void Open(LineSettings? settings, string who)
    {
        lock (this)
        {
            if (settings != null) Settings = settings.Clone();
            if (IsOpen) CloseCore(who);
            var st = Settings;
            var port = new SerialPort(Name, st.Baud, st.Parity, st.ByteSize, st.StopBits)
            {
                Handshake = st.RtsCts ? (st.XonXoff ? Handshake.RequestToSendXOnXOff : Handshake.RequestToSend)
                                      : (st.XonXoff ? Handshake.XOnXOff : Handshake.None),
                ReadTimeout = 100,
                WriteTimeout = 5000,
                ReadBufferSize = 1 << 16,
                DtrEnable = true,
                RtsEnable = !st.RtsCts,
            };
            port.Open();
            _port = port;
            Error = "";
            OpenedBy = who;
            Log($"\n---- bench opened {DateTime.Now:yyyy-MM-dd HH:mm:ss} {Describe()} by {who} ----\n");
            var t = new Thread(() => Pump(port)) { IsBackground = true, Name = "serial " + Name };
            t.Start();
        }
        Changed?.Invoke(this);
    }

    public void Close(string who)
    {
        lock (this) CloseCore(who);
        Changed?.Invoke(this);
    }

    void CloseCore(string who)
    {
        var port = _port;
        _port = null;
        if (port == null) return;
        try { port.Close(); } catch { }
        port.Dispose();
        Log($"\n---- bench closed {DateTime.Now:yyyy-MM-dd HH:mm:ss} {Name} by {who} ----\n");
    }

    public void Configure(LineSettings settings, string who)
    {
        lock (this)
        {
            Settings = settings.Clone();
            var p = _port;
            if (p != null && p.IsOpen)
            {
                p.BaudRate = settings.Baud; p.DataBits = settings.ByteSize; p.Parity = settings.Parity; p.StopBits = settings.StopBits;
                p.Handshake = settings.RtsCts ? (settings.XonXoff ? Handshake.RequestToSendXOnXOff : Handshake.RequestToSend)
                                              : (settings.XonXoff ? Handshake.XOnXOff : Handshake.None);
                Log($"\n---- bench reconfigured {Describe()} by {who} ----\n");
            }
        }
        Changed?.Invoke(this);
    }

    void Pump(SerialPort port)
    {
        var chunk = new byte[4096];
        while (_port == port)
        {
            int n;
            try
            {
                n = port.Read(chunk, 0, chunk.Length);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception e)
            {
                if (_port != port) break;
                Error = e.Message;
                Thread.Sleep(1000);
                continue;
            }
            if (n <= 0) continue;
            var data = chunk.AsSpan(0, n).ToArray();
            Append(data);
            Log(data);
            lock (Term) Term.Feed(data);
            Received?.Invoke(this, data);
        }
    }

    void Append(byte[] data)
    {
        lock (_gate)
        {
            _history.Append(data, DateTime.Now);
            Monitor.PulseAll(_gate);
        }
    }

    /// The bytes from `from` to `to`; past Most, only the newest Most, after
    /// a note of what was left out and where to page back to it.
    byte[] Capped(long from, long to)
    {
        from = Math.Max(from, _history.Start);
        if (to - from <= Most) return _history.Read(from, to);
        long cut = to - Most;
        var note = System.Text.Encoding.Latin1.GetBytes(
            $"[{cut - from} earlier bytes left out: serial_history from={from} pages through them]\n");
        return note.Concat(_history.Read(cut, to)).ToArray();
    }

    /// Bytes from `from` to `to` exactly, as held (for serial_history).
    public byte[] Read(long from, long to) { lock (_gate) return _history.Read(from, to); }

    /// Roughly when the byte at `at` arrived.
    public DateTime? When(long at) { lock (_gate) return _history.When(at); }

    /// Where `needle` next (or, backwards, last) appears in what is held.
    public long Find(long from, long to, byte[] needle, bool backwards)
    {
        lock (_gate) return backwards ? _history.FindLast(from, to, needle) : _history.Find(from, to, needle);
    }

    /// <summary>Sends bytes; `who` is written to the log beside them.</summary>
    public void Send(byte[] data, string who)
    {
        var p = _port;
        if (p == null || !p.IsOpen) throw new InvalidOperationException($"{Name} is not open");
        lock (p) p.Write(data, 0, data.Length);
        Sent?.Invoke(this, data, who);
        if (who != "terminal" && who != "window")
            Log(Encoding.Latin1.GetBytes($"\n<<< [{who}] ") .Concat(data).Concat(new byte[] { (byte)'\n' }).ToArray());
    }

    /// <summary>Holds the line in BREAK: the machine sees a long run of zero bits.</summary>
    public void Break(TimeSpan time)
    {
        var p = _port;
        if (p == null || !p.IsOpen) throw new InvalidOperationException($"{Name} is not open");
        p.BreakState = true;
        Thread.Sleep(time);
        p.BreakState = false;
    }

    /// <summary>What arrived from `from` on (the newest Most of it at most); `from` is moved to the end.</summary>
    public byte[] Take(ref long from)
    {
        lock (_gate)
        {
            var outp = Capped(from, _history.End);
            from = _history.End;
            return outp;
        }
    }

    /// <summary>Waits until `needle` shows up after `from`, or the time is up. Moves `from` past what it returns.</summary>
    public (bool found, byte[] text) Wait(ref long from, byte[] needle, TimeSpan time, CancellationToken cancel = default)
    {
        var end = DateTime.UtcNow + time;
        lock (_gate)
        {
            long searched = Math.Max(from, _history.Start);  // no match starts before this
            while (true)
            {
                long start = Math.Max(from, _history.Start);
                searched = Math.Max(searched, start);
                // ONLY WHAT IS NEW is searched each time round.
                long at = _history.Find(searched, _history.End, needle);
                if (at >= 0)
                {
                    from = at + needle.Length;
                    return (true, Capped(start, from));
                }
                searched = Math.Max(start, _history.End - needle.Length + 1);
                var left = end - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || cancel.IsCancellationRequested)
                {
                    from = _history.End;
                    return (false, Capped(start, from));
                }
                Monitor.Wait(_gate, left < TimeSpan.FromMilliseconds(500) ? left : TimeSpan.FromMilliseconds(500));
            }
        }
    }

    public byte[] Tail(int chars)
    {
        lock (_gate) return _history.Read(_history.End - Math.Clamp(chars, 0, Most), _history.End);
    }
}
