// Magewell Pro Capture cards through their own SDK (LibMWCapture.dll, which
// the Magewell driver installs in System32), for the lowest latency those
// cards can give: the card DMAs each frame into our memory in stripes of 64
// lines while it is still arriving, and we are told the moment its last
// stripe lands. This is Magewell's documented low-latency procedure
// (third_party/magewell-capture-sdk-*, Examples/VC++/low_latency_view,
// low_latency_capture.cpp), declared here from the SDK's C headers:
// LibMWCapture/MWCapture.h, MWProCapture.h, MWCaptureExtension.h, all
// #pragma pack(1).

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CorsacBench;

public static class MW
{
    const string Dll = "LibMWCapture.dll";

    public const ulong NotifyVideoInputSourceChange = 0x0004;  // MWCAP_NOTIFY_VIDEO_INPUT_SOURCE_CHANGE
    public const ulong NotifyInputSpecificChange = 0x0010;     // MWCAP_NOTIFY_INPUT_SPECIFIC_CHANGE
    public const ulong NotifyVideoSignalChange = 0x0020;       // MWCAP_NOTIFY_VIDEO_SIGNAL_CHANGE
    public const ulong NotifyVideoFrameBuffering = 0x0100;     // MWCAP_NOTIFY_VIDEO_FRAME_BUFFERING
    public const ulong NotifyVideoFrameBuffered = 0x0400;      // MWCAP_NOTIFY_VIDEO_FRAME_BUFFERED
    public const uint FourccBgra = 'B' | ('G' << 8) | ('R' << 16) | ((uint)'A' << 24);

    public enum Result { Succeeded = 0, Failed, InvalidParams }
    public enum SignalState { None = 0, Unsupported, Locking, Locked }
    [Flags] public enum InputType : uint { None = 0, Hdmi = 0x01, Vga = 0x02, Sdi = 0x04, Component = 0x08, Cvbs = 0x10, YC = 0x20 }

    /// MWCAP_VIDEO_TIMING: one way to read an analog line (MWCaptureExtension.h).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public record struct Timing
    {
        public uint dwType;
        public uint dwPixelClock;
        public byte bInterlaced, bySyncType, bHSPolarity, bVSPolarity;
        public ushort wHActive, wHFrontPorch, wHSyncWidth, wHBackPorch;
        public ushort wVActive, wVFrontPorch, wVSyncWidth, wVBackPorch;

        public int HTotal => wHActive + wHFrontPorch + wHSyncWidth + wHBackPorch;
        public override string ToString() => $"{wHActive}x{wVActive} ({HTotal} per line, {dwPixelClock / 1e6:0.###} MHz)";
    }

    /// MWCAP_VIDEO_TIMING_ARRAY (MWUSBCaptureExtension.h).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TimingArray
    {
        public byte byNumTimings;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public Timing[] aTimings;
    }

    /// MWCAP_INPUT_SPECIFIC_STATUS, read raw: BOOLEAN bValid; DWORD
    /// dwVideoInputType; then a union whose VGA/component member is
    /// MWCAP_COMPONENT_SPECIFIC_STATUS: MWCAP_VIDEO_SYNC_INFO (12 bytes),
    /// BOOLEAN bTriLevelSync, MWCAP_VIDEO_TIMING videoTiming (at byte 18).
    public static (InputType type, Timing? timing) InputStatus(IntPtr channel)
    {
        var raw = new byte[1024];
        if (MWGetInputSpecificStatus(channel, raw) != Result.Succeeded || raw[0] == 0) return (InputType.None, null);
        var type = (InputType)BitConverter.ToUInt32(raw, 1);
        if ((type & (InputType.Vga | InputType.Component)) == 0) return (type, null);
        return (type, MemoryMarshal.Read<Timing>(raw.AsSpan(18)));
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ChannelInfo
    {
        public ushort wFamilyID;
        public ushort wProductID;
        public sbyte chHardwareVersion;
        public byte byFirmwareID;
        public uint dwFirmwareVersion;
        public uint dwDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szFamilyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szProductName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szFirmwareName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string szBoardSerialNo;
        public byte byBoardIndex;
        public byte byChannelIndex;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SignalStatus
    {
        public SignalState state;
        public int x, y, cx, cy, cxTotal, cyTotal;
        public byte bInterlaced;
        public uint dwFrameDuration;            // 100 ns units, per frame (per field when interlaced)
        public int nAspectX, nAspectY;
        public byte bSegmentedFrame;
        public int frameType, colorFormat, quantRange, satRange;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BufferInfo
    {
        public uint cMaxFrames;
        public byte iNewestBuffering;
        public byte iBufferingFieldIndex;
        public byte iNewestBuffered;
        public byte iBufferedFieldIndex;
        public byte iNewestBufferedFullFrame;
        public uint cBufferedFullFrames;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CaptureStatus
    {
        public ulong pvContext;
        public byte bPhysicalAddress;
        public ulong pvFrame;                   // union with liPhysicalAddress
        public int iFrame;
        public byte bFrameCompleted;
        public ushort cyCompleted;
        public ushort cyCompletedPrev;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FrameInfo
    {
        public int state;
        public byte bInterlaced, bSegmentedFrame, bTopFieldFirst, bTopFieldInverted;
        public int cx, cy, nAspectX, nAspectY;
        public long fieldStart0, fieldStart1;
        public long fieldBuffered0, fieldBuffered1;
        public uint timecode0, timecode1;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int MWCaptureInitInstance();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void MWCaptureExitInstance();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWRefreshDevice();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int MWGetChannelCount();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWGetChannelInfoByIndex(int nIndex, ref ChannelInfo info);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern Result MWGetDevicePath(int nIndex, StringBuilder path);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern IntPtr MWOpenChannelByPath(string path);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void MWCloseChannel(IntPtr channel);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWGetVideoSignalStatus(IntPtr channel, ref SignalStatus status);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWStartVideoCapture(IntPtr channel, IntPtr hEvent);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWStopVideoCapture(IntPtr channel);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern ulong MWRegisterNotify(IntPtr channel, IntPtr hEvent, ulong enableBits);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWUnregisterNotify(IntPtr channel, ulong hNotify);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWGetNotifyStatus(IntPtr channel, ulong hNotify, out ulong status);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWGetVideoBufferInfo(IntPtr channel, ref BufferInfo info);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWGetVideoFrameInfo(IntPtr channel, byte i, ref FrameInfo info);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWGetVideoCaptureStatus(IntPtr channel, ref CaptureStatus status);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWGetDeviceTime(IntPtr channel, out long time);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWGetInputSpecificStatus(IntPtr channel, byte[] status);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWGetPreferredVideoTimings(IntPtr channel, ref TimingArray timings);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWSetVideoTiming(IntPtr channel, ref Timing timing);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWPinVideoBuffer(IntPtr channel, IntPtr buffer, uint size);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern Result MWUnpinVideoBuffer(IntPtr channel, IntPtr buffer);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern Result MWCaptureVideoFrameToVirtualAddressEx(
        IntPtr channel, int iFrame, IntPtr pbFrame, uint cbFrame, uint cbStride, byte bBottomUp, ulong pvContext,
        uint dwFOURCC, int cx, int cy, uint dwProcessSwitchs, int cyPartialNotify,
        IntPtr hOSDImage, IntPtr pOSDRects, int cOSDRects,
        short sContrast, short sBrightness, short sSaturation, short sHue,
        int deinterlaceMode, int aspectRatioConvertMode, IntPtr pRectSrc, IntPtr pRectDest,
        int nAspectX, int nAspectY, int colorFormat, int quantRange, int satRange);

    static bool _started, _present;

    /// Whether the SDK is here and the driver answers. Once.
    public static bool Available()
    {
        lock (typeof(MW))
        {
            if (_started) return _present;
            _started = true;
            try
            {
                MWCaptureInitInstance();
                MWRefreshDevice();
                _present = true;
            }
            catch (DllNotFoundException) { _present = false; }
            catch (EntryPointNotFoundException) { _present = false; }
            return _present;
        }
    }

    /// Every Magewell channel, named as its DirectShow device is: "00-1 Pro
    /// Capture Dual DVI" for board 0 channel 1 of a two-channel card, "01 Pro
    /// Capture AIO" for board 1 of a one-channel card.
    public static List<(int index, string name, string path)> Channels()
    {
        var list = new List<(int, string, string)>();
        if (!Available()) return list;
        lock (typeof(MW))
        {
            MWRefreshDevice();
            int n = MWGetChannelCount();
            var infos = new List<(int i, ChannelInfo info, string path)>();
            for (int i = 0; i < n; i++)
            {
                var info = new ChannelInfo();
                if (MWGetChannelInfoByIndex(i, ref info) != Result.Succeeded) continue;
                var path = new StringBuilder(260);
                if (MWGetDevicePath(i, path) != Result.Succeeded) continue;
                infos.Add((i, info, path.ToString()));
            }
            foreach (var (i, info, path) in infos)
            {
                bool several = infos.Count(x => x.info.byBoardIndex == info.byBoardIndex) > 1;
                string name = several ? $"{info.byBoardIndex:X2}-{info.byChannelIndex} {info.szProductName}" : $"{info.byBoardIndex:X2} {info.szProductName}";
                list.Add((i, name, path));
            }
        }
        return list;
    }

    /// The Magewell channel a DirectShow device name ("Video (00-1 Pro Capture Dual DVI)") is.
    public static string? PathFor(string device)
    {
        foreach (var (_, name, path) in Channels())
            if (device.Contains(name, StringComparison.OrdinalIgnoreCase)) return path;
        return null;
    }
}

/// One Magewell channel, captured in low-latency mode on a thread of its own.
public sealed class MagewellSource : VideoSource
{
    readonly string _path;
    Thread? _thread;
    volatile bool _run;
    volatile bool _reopen;

    public MagewellSource(string device, string path) : base(device) { _path = path; }

    public override void Start()
    {
        if (_thread != null) return;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "magewell " + Device, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public override void Stop()
    {
        _run = false;
        _thread?.Join(2000);
        _thread = null;
    }

    public override void Reopen() => _reopen = true;

    public override IReadOnlyList<(int w, int h)> Sizes => new[]
    {
        (640, 350), (640, 400), (640, 480), (720, 400), (720, 480), (800, 600), (1024, 768),
        (1152, 864), (1280, 720), (1280, 960), (1280, 1024), (1600, 1200), (1920, 1080), (1920, 1200),
    };

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateEvent(IntPtr attr, bool manual, bool initial, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);

    void Loop()
    {
        while (_run)
        {
            // Lingering with nobody watching: the channel stays closed.
            if (Users <= 0) { Thread.Sleep(50); continue; }
            // A new mode reopens at once; only a failure waits before trying again.
            try { Session(); }
            catch (Exception e) { Error = e.Message; if (_run) Thread.Sleep(500); }
        }
    }

    /// One open of the channel, until it must be opened again (a new size,
    /// a signal change, a stop). The loop is the SDK example's.
    void Session()
    {
        _reopen = false;
        IntPtr channel = MW.MWOpenChannelByPath(_path);
        if (channel == IntPtr.Zero) throw new InvalidOperationException("could not open the Magewell channel");
        Judgement? judge;
        IntPtr captureEvent = CreateEvent(IntPtr.Zero, false, false, null);
        IntPtr notifyEvent = CreateEvent(IntPtr.Zero, false, false, null);
        ulong notify = 0;
        var pinned = new List<VideoFrame>();
        bool capturing = false;
        try
        {
            var signal = new MW.SignalStatus();
            MW.MWGetVideoSignalStatus(channel, ref signal);
            bool locked = signal.state == MW.SignalState.Locked;
            int w = Settings.CaptureW > 0 ? Settings.CaptureW : locked ? signal.cx : 1024;
            int h = Settings.CaptureH > 0 ? Settings.CaptureH : locked ? signal.cy : 768;
            NativeW = locked ? signal.cx : 0;
            NativeH = locked ? signal.cy : 0;
            Interlaced = signal.bInterlaced != 0;
            SignalHz = locked && signal.dwFrameDuration > 0 ? (Interlaced ? 20_000_000.0 : 10_000_000.0) / signal.dwFrameDuration : 0;
            Error = locked ? "" : signal.state == MW.SignalState.None ? "no signal" : $"signal {signal.state.ToString().ToLowerInvariant()}";

            // AN ANALOG INPUT CARRIES NO PIXEL CLOCK. Where several standard
            // timings fit the same sync (720x400 text and 640x400 graphics,
            // and their like at every resolution), the card lists them and
            // the picture says which is right: see Judge.
            var (inputType, timing) = MW.InputStatus(channel);
            InputKind = inputType;
            judge = null;
            if (locked && timing != null)
            {
                var list = new MW.TimingArray { aTimings = new MW.Timing[8] };
                if (MW.MWGetPreferredVideoTimings(channel, ref list) == MW.Result.Succeeded)
                {
                    var candidates = list.aTimings.Take(Math.Min((int)list.byNumTimings, 8)).Distinct().ToList();
                    if (!candidates.Contains(timing.Value)) candidates.Insert(0, timing.Value);
                    if (candidates.Count > 1)
                    {
                        string key = string.Join("|", candidates.OrderBy(t => t.dwPixelClock).ThenBy(t => t.wHActive));
                        if (!_judgements.TryGetValue(key, out judge)) _judgements[key] = judge = new Judgement(candidates);
                        judge.Current = candidates.IndexOf(timing.Value);
                        if (judge.Asked >= 0 && judge.Asked != judge.Current)
                        {
                            // The card would not read the line that way: never ask again.
                            judge.Scores[judge.Asked] = double.MaxValue;
                            if (judge.Best == judge.Asked) judge.Best = -1;
                        }
                        judge.Asked = -1;
                        // A settled choice for this sync, from before: straight to it.
                        if (judge.Best >= 0 && judge.Best != judge.Current && SetTiming(channel, judge, judge.Best)) return;
                    }
                }
            }
            TimingText = timing == null ? "" : $"{inputType.ToString().ToUpperInvariant()} {timing}" +
                (judge == null ? "" : judge.Best == judge.Current ? $", best of {judge.Candidates.Count}" : $", judging {judge.Candidates.Count}");

            // THREE PINNED BUFFERS: the card writes one while the display
            // copies from the newest complete one (or the one before it, if
            // it began copying just as a new one completed).
            for (int i = 0; i < 3; i++)
            {
                var f = VideoFrame.Make(w, h);
                MW.MWPinVideoBuffer(channel, f.Address, (uint)f.Pixels.Length);
                pinned.Add(f);
            }

            if (MW.MWStartVideoCapture(channel, captureEvent) != MW.Result.Succeeded) throw new InvalidOperationException("the card would not start capturing");
            capturing = true;
            notify = MW.MWRegisterNotify(channel, notifyEvent, MW.NotifyVideoFrameBuffering | MW.NotifyVideoSignalChange
                | MW.NotifyInputSpecificChange | MW.NotifyVideoInputSourceChange);
            if (notify == 0) throw new InvalidOperationException("the card would not take a notification");

            long count = 0;
            var measured = Stopwatch.StartNew();
            long measuredAt = 0;
            while (_run && !_reopen && Users > 0)
            {
                if (WaitForSingleObject(notifyEvent, locked ? 1000u : 250u) != 0)
                {
                    if (!locked) { MW.MWGetVideoSignalStatus(channel, ref signal); if (signal.state == MW.SignalState.Locked) return; }
                    continue;
                }
                MW.MWGetNotifyStatus(channel, notify, out ulong status);
                if ((status & (MW.NotifyInputSpecificChange | MW.NotifyVideoInputSourceChange)) != 0)
                {
                    // Another input, or the card read the analog line another way.
                    var (_, t) = MW.InputStatus(channel);
                    if (t != timing) return;
                }
                if ((status & MW.NotifyVideoSignalChange) != 0)
                {
                    // A NEW MODE: opened again at its size (when following the signal).
                    var now = new MW.SignalStatus();
                    MW.MWGetVideoSignalStatus(channel, ref now);
                    if (now.state != signal.state || now.cx != signal.cx || now.cy != signal.cy || now.dwFrameDuration != signal.dwFrameDuration || now.bInterlaced != signal.bInterlaced)
                        return;
                }
                if ((status & MW.NotifyVideoFrameBuffering) == 0 || !locked) continue;

                var buffer = new MW.BufferInfo();
                if (MW.MWGetVideoBufferInfo(channel, ref buffer) != MW.Result.Succeeded) continue;
                // THE FRAME AFTER THE NEWEST COMPLETE ONE: never the buffer a
                // display may still be reading.
                var frame = pinned.First(f => !ReferenceEquals(f, Latest) && !ReferenceEquals(f, _previous));
                byte slot = buffer.iNewestBuffering;

                // THE FRAME STILL ARRIVING, in 64-line stripes, as it is
                // buffered on the card: weave (no deinterlacing), no aspect
                // conversion, no picture adjustment, colour as the source is.
                var r = MW.MWCaptureVideoFrameToVirtualAddressEx(channel, slot, frame.Address, (uint)frame.Pixels.Length, (uint)frame.Stride, 0, 0,
                    MW.FourccBgra, frame.Width, frame.Height, 0, 64, IntPtr.Zero, IntPtr.Zero, 0,
                    100, 0, 100, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 0, 0);
                if (r != MW.Result.Succeeded) continue;
                Begin(frame);

                // Each stripe is shown as it lands; the display need not wait
                // for the bottom of the frame to show the top.
                bool done = false;
                while (_run)
                {
                    if (WaitForSingleObject(captureEvent, 1000) != 0) break;
                    var cs = new MW.CaptureStatus();
                    if (MW.MWGetVideoCaptureStatus(channel, ref cs) != MW.Result.Succeeded) break;
                    if (cs.bFrameCompleted != 0) { done = true; break; }
                    Progress(frame, cs.cyCompleted);
                }
                if (!done) { Abandon(); continue; }

                // HOW LONG FROM THE FRAME'S FIRST LINE ON THE WIRE TO IT BEING
                // OURS, by the card's own clock.
                var fi = new MW.FrameInfo();
                if (MW.MWGetVideoFrameInfo(channel, slot, ref fi) == MW.Result.Succeeded && MW.MWGetDeviceTime(channel, out long nowTime) == MW.Result.Succeeded)
                    CaptureLatencyMs = (nowTime - fi.fieldStart0) / 10_000.0;

                count++;
                if (measured.ElapsedMilliseconds - measuredAt >= 1000)
                {
                    Fps = (count - FramesAtMeasure) * 1000.0 / (measured.ElapsedMilliseconds - measuredAt);
                    FramesAtMeasure = count;
                    measuredAt = measured.ElapsedMilliseconds;
                }
                _previous = Latest;
                Publish(frame);
                if (judge != null && Settings.CaptureW == 0 && !Interlaced && Judge(channel, judge, frame)) return;
            }
        }
        finally
        {
            if (notify != 0) MW.MWUnregisterNotify(channel, notify);
            if (capturing) MW.MWStopVideoCapture(channel);
            foreach (var f in pinned) MW.MWUnpinVideoBuffer(channel, f.Address);
            MW.MWCloseChannel(channel);
            CloseHandle(captureEvent);
            CloseHandle(notifyEvent);
        }
    }

    long FramesAtMeasure;
    VideoFrame? _previous;

    // ---- which analog timing ------------------------------------------------

    /// The candidate timings for one sync, and what the picture said of each.
    sealed class Judgement(List<MW.Timing> candidates)
    {
        public readonly List<MW.Timing> Candidates = candidates;
        public readonly double?[] Scores = new double?[candidates.Count];
        public int Current = -1, Best = -1, Asked = -1;
        public double BestScore;
        public readonly List<double> Evidence = new();
        public long MeasuredAt;
    }

    readonly Dictionary<string, Judgement> _judgements = new();

    /// Sampled at the right clock, a pixel edge falls between two samples;
    /// at a wrong one, samples straddle the edges and come out between the
    /// colours either side. On the live 720x400 text screen: 0.018 at the
    /// right timing, 0.29 to 0.52 at any wrong one.
    const double Crisp = 0.08, Smeared = 0.15;
    const int MinEdges = 2000;

    /// One look at a complete frame (a few times a second). True when the
    /// timing was changed and the channel must be opened again.
    bool Judge(IntPtr channel, Judgement j, VideoFrame f)
    {
        long now = Environment.TickCount64;
        if (now - j.MeasuredAt < 200) return false;
        j.MeasuredAt = now;
        var (score, edges) = Smear(f);
        if (edges < MinEdges) return false;           // a blank or flat screen says nothing
        j.Evidence.Add(score);
        if (j.Evidence.Count < 3) return false;
        double mean = j.Evidence.Average();
        j.Evidence.Clear();

        if (j.Best >= 0 && j.Best == j.Current)
        {
            // Settled; but the machine may have changed mode inside the same
            // sync (text to graphics). Judged afresh if the picture smears.
            if (mean <= Math.Max(Smeared, j.BestScore * 1.5 + 0.03)) { j.BestScore = Math.Min(j.BestScore, mean); return false; }
            Array.Clear(j.Scores);
            j.Best = -1;
        }

        j.Scores[j.Current] = mean;
        if (mean < Crisp) { Settle(j, j.Current, mean); return false; }
        int next = Array.FindIndex(j.Scores, s => s == null);
        if (next < 0 && j.Scores.All(s => s == double.MaxValue)) return false;
        if (next >= 0) return SetTiming(channel, j, next);
        int best = 0;
        for (int i = 1; i < j.Scores.Length; i++) if (j.Scores[i] < j.Scores[best]) best = i;
        Settle(j, best, j.Scores[best]!.Value);
        return best != j.Current && SetTiming(channel, j, best);
    }

    void Settle(Judgement j, int best, double score)
    {
        j.Best = best;
        j.BestScore = score;
        TimingText = $"{InputKind.ToString().ToUpperInvariant()} {j.Candidates[best]}, best of {j.Candidates.Count}";
    }

    bool SetTiming(IntPtr channel, Judgement j, int i)
    {
        var t = j.Candidates[i];
        if (MW.MWSetVideoTiming(channel, ref t) != MW.Result.Succeeded) { j.Scores[i] = double.MaxValue; return false; }
        j.Asked = i;
        j.Evidence.Clear();
        return true;
    }

    /// The share of pixel edges whose middle sample lies between the colours
    /// either side of it, over rows spread down the frame.
    static unsafe (double score, int edges) Smear(VideoFrame f)
    {
        int edges = 0, smeared = 0;
        int step = Math.Max(1, f.Height / 150);
        fixed (byte* p0 = f.Pixels)
        {
            for (int y = 0; y < f.Height; y += step)
            {
                byte* row = p0 + y * f.Stride;
                int L(int x) { byte* p = row + x * 4; return (p[2] * 2 + p[1] * 5 + p[0]) >> 3; }
                int a = L(0), b = L(1);
                for (int x = 2; x < f.Width; x++)
                {
                    int c = L(x);
                    int lo = Math.Min(a, c), hi = Math.Max(a, c);
                    if (hi - lo >= 64)
                    {
                        edges++;
                        int q = (hi - lo) >> 2;
                        if (b > lo + q && b < hi - q) smeared++;
                    }
                    a = b; b = c;
                }
            }
        }
        return (edges == 0 ? 0 : (double)smeared / edges, edges);
    }
}
