// A capture device's own low-latency source (today: Magewell channels through
// the card's SDK). One per device, shared by every view of it and by
// vga_capture, running while anything uses it. Every other device keeps the
// DirectShow path through vgagrab.py (Vga.cs), which ScreenView drives itself.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CorsacBench;

/// One captured frame: 32-bit BGRA, rows top down, in memory the garbage
/// collector will not move (the card DMAs straight into it).
public sealed class VideoFrame
{
    public required byte[] Pixels;
    public required int Width, Height, Stride;
    public long Seq;
    /// How many rows, from the top, hold this frame's pixels so far; Height once complete.
    public volatile int Rows;
    /// Stopwatch ticks when the frame was complete.
    public long Completed;

    public IntPtr Address => Marshal.UnsafeAddrOfPinnedArrayElement(Pixels, 0);

    public static VideoFrame Make(int w, int h) =>
        new() { Pixels = GC.AllocateArray<byte>(w * h * 4, pinned: true), Width = w, Height = h, Stride = w * 4 };

    /// A copy as a bitmap, for the clipboard, a file or vga_capture.
    public Bitmap ToBitmap(Rectangle? part = null)
    {
        var r = part ?? new Rectangle(0, 0, Width, Height);
        var b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppRgb);
        var bits = b.LockBits(new Rectangle(0, 0, r.Width, r.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < r.Height; y++)
                Marshal.Copy(Pixels, (r.Y + y) * Stride + r.X * 4, bits.Scan0 + y * bits.Stride, r.Width * 4);
        }
        finally { b.UnlockBits(bits); }
        return b;
    }
}

public abstract class VideoSource
{
    public readonly string Device;
    public ScreenSettings Settings = new();
    public volatile string Error = "";
    public int NativeW, NativeH;
    public bool Interlaced;
    public double SignalHz, Fps, CaptureLatencyMs = -1;
    VideoFrame? _latest, _current;
    long _seq;

    /// Raised on the capture thread the moment a frame is complete.
    public event Action<VideoFrame>? Frame;
    /// Raised on the capture thread whenever more rows of the current frame land.
    public event Action<VideoFrame>? Rows;

    protected VideoSource(string device) { Device = device; }

    public abstract void Start();
    public abstract void Stop();
    /// Opens the device again with the settings as they now are.
    public abstract void Reopen();
    public abstract IReadOnlyList<(int w, int h)> Sizes { get; }

    /// The newest complete frame.
    public VideoFrame? Latest => _latest;
    /// The frame arriving now, as far as it has (its Rows), else the newest
    /// complete one: what a display shows to be as current as it can be.
    public VideoFrame? Current => _current ?? _latest;
    public long Seq => Interlocked.Read(ref _seq);

    /// A frame starts arriving into `f`.
    protected void Begin(VideoFrame f)
    {
        f.Rows = 0;
        f.Seq = Seq + 1;
        _current = f;
    }

    /// More of it has arrived.
    protected void Progress(VideoFrame f, int rows)
    {
        if (rows <= f.Rows) return;
        f.Rows = Math.Min(rows, f.Height);
        Rows?.Invoke(f);
    }

    /// It is complete.
    protected void Publish(VideoFrame f)
    {
        f.Completed = Stopwatch.GetTimestamp();
        f.Seq = Interlocked.Increment(ref _seq);
        f.Rows = f.Height;
        _latest = f;
        Rows?.Invoke(f);
        Frame?.Invoke(f);
        lock (_waiters) Monitor.PulseAll(_waiters);
    }

    /// The frame that was arriving did not complete.
    protected void Abandon() => _current = null;

    readonly object _waiters = new();

    /// The next frame after `since`, waiting up to `ms`.
    public VideoFrame? Next(long since, int ms)
    {
        var end = Environment.TickCount64 + ms;
        lock (_waiters)
        {
            while (Seq <= since && Environment.TickCount64 < end) Monitor.Wait(_waiters, (int)Math.Max(1, end - Environment.TickCount64));
        }
        return Seq > since ? _latest : null;
    }

    // ---- sharing ----------------------------------------------------------

    public int Users;
    static readonly Dictionary<string, VideoSource> Sources = new();
    static readonly Dictionary<string, System.Threading.Timer> Lingering = new();

    /// The device's native source, started if it was not running, or null
    /// when the device has none (it then stays on DirectShow). Release when done.
    public static VideoSource? Use(string device, ScreenSettings? settings = null)
    {
        lock (Sources)
        {
            if (Lingering.Remove(device, out var t)) t.Dispose();
            if (!Sources.TryGetValue(device, out var s))
            {
                string? path = MW.PathFor(device);
                if (path == null) return null;
                s = new MagewellSource(device, path);
                Sources[device] = s;
            }
            if (settings != null) s.Settings = settings;
            s.Users++;
            s.Start();
            return s;
        }
    }

    /// Done with it; it stops `linger` later if nobody else wants it by then.
    public static void Release(VideoSource s, int linger = 0)
    {
        lock (Sources)
        {
            if (--s.Users > 0) return;
            if (linger <= 0) { StopNow(s); return; }
            Lingering[s.Device] = new System.Threading.Timer(_ =>
            {
                lock (Sources)
                {
                    if (s.Users > 0) return;
                    Lingering.Remove(s.Device);
                    StopNow(s);
                }
            }, null, linger, Timeout.Infinite);
        }
    }

    static void StopNow(VideoSource s)
    {
        Sources.Remove(s.Device);
        s.Stop();
    }

    public static void StopAll()
    {
        List<VideoSource> all;
        lock (Sources) { all = Sources.Values.ToList(); Sources.Clear(); }
        foreach (var s in all) s.Stop();
    }
}
