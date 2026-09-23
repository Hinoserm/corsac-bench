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

    /// <summary>Held by whoever is running a command, so two clients' commands do not interleave.</summary>
    public readonly SemaphoreSlim Conversation = new(1, 1);
    public string ConversationHolder = "";

    const int KeepBytes = 16 << 20;
    readonly object _gate = new();
    byte[] _buf = new byte[1 << 16];
    int _count;         // bytes held in _buf
    long _base;         // absolute offset of _buf[0]
    SerialPort? _port;
    FileStream? _log;

    /// <summary>Raised on the reader thread with every chunk received.</summary>
    public event Action<Line, byte[]>? Received;
    /// <summary>Raised when the port opens, closes or changes settings.</summary>
    public event Action<Line>? Changed;

    public Line(string name)
    {
        Name = name;
        Term.Reply = data => { try { Send(data, "terminal"); } catch { } };
    }

    public bool IsOpen => _port?.IsOpen == true;
    public long End { get { lock (_gate) return _base + _count; } }
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
            if (_count + data.Length > _buf.Length)
            {
                if (_count + data.Length > KeepBytes)
                {
                    int drop = _count + data.Length - KeepBytes / 2;
                    drop = Math.Min(drop, _count);
                    Buffer.BlockCopy(_buf, drop, _buf, 0, _count - drop);
                    _count -= drop;
                    _base += drop;
                }
                if (_count + data.Length > _buf.Length)
                {
                    var bigger = new byte[Math.Max(_buf.Length * 2, _count + data.Length)];
                    Buffer.BlockCopy(_buf, 0, bigger, 0, _count);
                    _buf = bigger;
                }
            }
            Buffer.BlockCopy(data, 0, _buf, _count, data.Length);
            _count += data.Length;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Sends bytes; `who` is written to the log beside them.</summary>
    public void Send(byte[] data, string who)
    {
        var p = _port;
        if (p == null || !p.IsOpen) throw new InvalidOperationException($"{Name} is not open");
        lock (p) p.Write(data, 0, data.Length);
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

    /// <summary>What arrived from `from` on; `from` is moved to the end. Bytes dropped from the buffer are skipped.</summary>
    public byte[] Take(ref long from)
    {
        lock (_gate)
        {
            long start = Math.Max(from, _base);
            var outp = _buf.AsSpan((int)(start - _base), (int)(_base + _count - start)).ToArray();
            from = _base + _count;
            return outp;
        }
    }

    /// <summary>Waits until `needle` shows up after `from`, or the time is up. Moves `from` past what it returns.</summary>
    public (bool found, byte[] text) Wait(ref long from, byte[] needle, TimeSpan time, CancellationToken cancel = default)
    {
        var end = DateTime.UtcNow + time;
        lock (_gate)
        {
            while (true)
            {
                long start = Math.Max(from, _base);
                var have = _buf.AsSpan((int)(start - _base), (int)(_base + _count - start));
                int at = have.IndexOf(needle);
                if (at >= 0)
                {
                    var outp = have[..(at + needle.Length)].ToArray();
                    from = start + at + needle.Length;
                    return (true, outp);
                }
                var left = end - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || cancel.IsCancellationRequested)
                {
                    from = _base + _count;
                    return (false, have.ToArray());
                }
                Monitor.Wait(_gate, left < TimeSpan.FromMilliseconds(500) ? left : TimeSpan.FromMilliseconds(500));
            }
        }
    }

    public byte[] Tail(int chars)
    {
        lock (_gate)
        {
            int n = Math.Min(chars, _count);
            return _buf.AsSpan(_count - n, n).ToArray();
        }
    }
}
