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
        /// How a setting or a tool names this timing.
        public string Spec => $"{wHActive}x{wVActive}/{HTotal}";
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
    public static (InputType type, Timing? timing, Sync sync) InputStatus(IntPtr channel)
    {
        var raw = new byte[1024];
        if (MWGetInputSpecificStatus(channel, raw) != Result.Succeeded || raw[0] == 0) return (InputType.None, null, default);
        var type = (InputType)BitConverter.ToUInt32(raw, 1);
        if ((type & (InputType.Vga | InputType.Component)) == 0) return (type, null, default);
        return (type, MemoryMarshal.Read<Timing>(raw.AsSpan(18)), MemoryMarshal.Read<Sync>(raw.AsSpan(5)));
    }

    /// MWCAP_VIDEO_SYNC_INFO: the sync as the card MEASURES it, whatever
    /// timing it is told to read the line with.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public record struct Sync
    {
        public byte bySyncType, bHSPolarity, bVSPolarity, bInterlaced;
        public uint dwFrameDuration;
        public ushort wVSyncLineCount, wFrameLineCount;

        public double Hz => dwFrameDuration > 0 ? (bInterlaced != 0 ? 2e7 : 1e7) / dwFrameDuration : 0;
        /// What names this sync: a VGA card says how many lines a mode has
        /// by its sync polarities (350: H+ V-, 400: H- V+, 480: H- V-).
        public string Key => $"H{(bHSPolarity != 0 ? '+' : '-')} V{(bVSPolarity != 0 ? '+' : '-')}{(bInterlaced != 0 ? " interlaced" : "")}, {wFrameLineCount} lines, {Math.Round(Hz)} Hz";
        public bool Fits(Timing t) => (t.bHSPolarity != 0) == (bHSPolarity != 0) && (t.bVSPolarity != 0) == (bVSPolarity != 0);
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
        long opening = Stopwatch.GetTimestamp();
        static double Ms(long since) => (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;
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
            // EVERY OPENING STARTS FROM BLACK: a new mode or a lost signal
            // leaves nothing of the old picture behind.
            Blank();
            NativeW = locked ? signal.cx : 0;
            NativeH = locked ? signal.cy : 0;
            AspectX = locked ? signal.nAspectX : 0;
            AspectY = locked ? signal.nAspectY : 0;
            Interlaced = locked && signal.bInterlaced != 0;
            Fps = 0;
            CaptureLatencyMs = -1;
            SignalHz = locked && signal.dwFrameDuration > 0 ? (Interlaced ? 20_000_000.0 : 10_000_000.0) / signal.dwFrameDuration : 0;
            Error = locked ? "" : signal.state == MW.SignalState.None ? "no signal" : $"signal {signal.state.ToString().ToLowerInvariant()}";
            if (!locked)
            {
                if (_unlockedAt == 0) _unlockedAt = Stopwatch.GetTimestamp();
                Note(Error);
            }
            else
            {
                Note($"locked {signal.cx}x{signal.cy} {SignalHz:0.##} Hz" + (_unlockedAt != 0 ? $", {Ms(_unlockedAt):0} ms after the lock was lost" : ""));
                _unlockedAt = 0;
            }

            // AN ANALOG INPUT CARRIES NO PIXEL CLOCK, and one sync fits several
            // timings. The card lists them. Which is right:
            //  - the number of lines comes from the sync polarities the card
            //    measures (a VGA card's own way of saying it), so only
            //    timings of that polarity are in the running;
            //  - the width comes from the picture (see Judge), starting from
            //    the card's own first choice;
            //  - or the person, or a session, has said which (Timings in the
            //    device's settings), and that is that.
            var (inputType, timing, sync) = MW.InputStatus(channel);
            InputKind = inputType;
            judge = null;
            string how = "";
            if (locked && timing != null)
            {
                var list = new MW.TimingArray { aTimings = new MW.Timing[8] };
                var candidates = MW.MWGetPreferredVideoTimings(channel, ref list) == MW.Result.Succeeded
                    ? list.aTimings.Take(Math.Min((int)list.byNumTimings, 8)).Distinct().ToList() : new List<MW.Timing>();
                if (!candidates.Contains(timing.Value)) candidates.Add(timing.Value);
                string key = sync.Key;
                lock (_judgements)
                    if (!_judgements.TryGetValue(key, out judge) || !judge.Candidates.SequenceEqual(candidates))
                        _judgements[key] = judge = new Judgement(candidates, sync);
                judge.Current = candidates.IndexOf(timing.Value);
                if (judge.Asked >= 0 && judge.Asked != judge.Current)
                {
                    // The card would not read the line that way: never ask again.
                    judge.Scores[judge.Asked] = double.MaxValue;
                    if (judge.Best == judge.Asked) judge.Best = -1;
                }
                // A round of trials goes from one opening to the next only by
                // way of a timing this asked for; any other opening (the machine
                // changed mode meanwhile) ends it.
                if (judge.Asked < 0) { judge.Home = -1; judge.ToTry.Clear(); }
                judge.Asked = -1;
                // Each opening is judged on its own frames.
                judge.Forget();
                judge.Skipped = 0;

                Settings.Timings.TryGetValue(key, out string? manual);
                int chosen = manual == null ? -1 : candidates.FindIndex(t => t.Spec == manual);
                judge.Manual = chosen >= 0 && judge.Scores[chosen] != double.MaxValue;
                int wanted = judge.Manual ? chosen
                    : judge.Best >= 0 ? judge.Best
                    : judge.Eligible.Contains(judge.Current) ? judge.Current
                    : judge.First;
                if (wanted != judge.Current && SetTiming(channel, judge, wanted)) return;
                how = judge.Manual ? "chosen by hand"
                    : manual != null && chosen >= 0 ? "the card refused the timing chosen by hand; automatic"
                    : judge.Eligible.Count <= 1 ? "the only timing of this sync's polarity"
                    : judge.Best == judge.Current ? $"automatic, {judge.Eligible.Count} timings fit" : $"judging {judge.Eligible.Count}";
                Report(key, judge);
            }
            else Timings = null;
            TimingText = timing == null ? "" : $"{inputType.ToString().ToUpperInvariant()} {timing}, sync {sync.Key}, {how}";

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
            Note($"capturing {w}x{h}, {Ms(opening):0} ms after starting to open the channel");
            // A lock gained between reading the status and asking to be told
            // of changes would otherwise wait for the next poll.
            if (!locked) { MW.MWGetVideoSignalStatus(channel, ref signal); if (signal.state == MW.SignalState.Locked) return; }
            bool first = true;

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
                    var (_, t, sy) = MW.InputStatus(channel);
                    if (t != timing || sy.Key != sync.Key) { Note($"the card reports another timing or sync: {t?.Spec ?? "none"}, {sy.Key}"); return; }
                }
                if ((status & MW.NotifyVideoSignalChange) != 0)
                {
                    // A NEW MODE: opened again at its size (when following the signal).
                    var now = new MW.SignalStatus();
                    MW.MWGetVideoSignalStatus(channel, ref now);
                    // The frame time is a measurement and wanders by a count or
                    // two; only a real difference (half a percent) is a new mode.
                    bool rate = Math.Abs((long)now.dwFrameDuration - signal.dwFrameDuration) > signal.dwFrameDuration / 200;
                    if (now.state != signal.state || now.cx != signal.cx || now.cy != signal.cy || rate || now.bInterlaced != signal.bInterlaced)
                    {
                        Note($"signal changed: {now.state.ToString().ToLowerInvariant()} {now.cx}x{now.cy}");
                        if (now.state != MW.SignalState.Locked && _unlockedAt == 0) _unlockedAt = Stopwatch.GetTimestamp();
                        return;
                    }
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
                {
                    // Only a time the frame could have taken: a slot the card
                    // has not stamped (just after locking) reads as nonsense.
                    double ms = (nowTime - fi.fieldStart0) / 10_000.0;
                    CaptureLatencyMs = ms >= 0 && ms < 1000 ? ms : -1;
                }

                count++;
                if (measured.ElapsedMilliseconds - measuredAt >= 1000)
                {
                    Fps = (count - FramesAtMeasure) * 1000.0 / (measured.ElapsedMilliseconds - measuredAt);
                    FramesAtMeasure = count;
                    measuredAt = measured.ElapsedMilliseconds;
                }
                _previous = Latest;
                Publish(frame);
                if (first) { first = false; Note($"first frame {Ms(opening):0} ms after starting to open the channel"); }
                if (judge != null && !judge.Manual && judge.Eligible.Count > 1 && Settings.CaptureW == 0 && !Interlaced && Judge(channel, judge, frame)) return;
            }
        }
        finally
        {
            long closing = Stopwatch.GetTimestamp();
            if (notify != 0) MW.MWUnregisterNotify(channel, notify);
            if (capturing) MW.MWStopVideoCapture(channel);
            foreach (var f in pinned) MW.MWUnpinVideoBuffer(channel, f.Address);
            MW.MWCloseChannel(channel);
            Note($"channel closed in {Ms(closing):0} ms");
            CloseHandle(captureEvent);
            CloseHandle(notifyEvent);
        }
    }

    long FramesAtMeasure;
    VideoFrame? _previous;
    long _unlockedAt;       // when the lock was lost, until it is found again

    // ---- which analog timing ------------------------------------------------

    /// The candidate timings for one sync, and what the picture said of each.
    sealed class Judgement(List<MW.Timing> candidates, MW.Sync sync)
    {
        public readonly List<MW.Timing> Candidates = candidates;
        public readonly MW.Sync Sync = sync;
        /// The candidates in the running: those of the measured polarity
        /// (the right number of lines); every one, if none is.
        public readonly List<int> Eligible = InTheRunning(candidates, sync);

        static List<int> InTheRunning(List<MW.Timing> candidates, MW.Sync sync)
        {
            var all = Enumerable.Range(0, candidates.Count).ToList();
            var fitting = all.Where(i => sync.Fits(candidates[i])).ToList();
            return fitting.Count > 0 ? fitting : all;
        }
        public readonly double?[] Scores = new double?[candidates.Count];
        public int Current = -1, Best = -1, Asked = -1;
        /// The timing in use was chosen by hand: nothing is judged.
        public bool Manual;
        /// Looks in a row that found a settled timing smeared in another's pattern.
        public int Smeary;
        /// Frames passed over since the timing was last asked for, and frames seen while settled.
        public int Skipped, Watched;
        /// A ROUND OF TRIALS: the timing it started from (-1 when none is
        /// running) and the timings still to try.
        public int Home = -1;
        public readonly Queue<int> ToTry = new();
        /// When each timing last lost a round: it is not tried again for a while.
        public readonly long[] LostAt = new long[candidates.Count];
        /// The card's own first choice among those in the running.
        public int First => Eligible.FirstOrDefault(i => Scores[i] != double.MaxValue, Eligible[0]);
        public double BestScore;
        public readonly List<double> Evidence = new();
        /// The pattern each timing would leave, summed over the looks in Evidence.
        public readonly double[] Pattern = new double[candidates.Count];

        public void Forget() { Evidence.Clear(); Array.Clear(Pattern); }
    }

    readonly Dictionary<string, Judgement> _judgements = new();

    // HOW THE WIDTH IS JUDGED.
    //
    // Sampled at the right clock, a pixel edge falls between two samples.
    // At a wrong one the samples drift across the pixels, and where a sample
    // straddles an edge it comes out between the colours either side: the
    // picture SMEARS (0.00 on a 720x400 text screen at the right timing,
    // 0.09 to 0.32 at the wrong one).
    //
    // But a film, a game or a scaled picture is soft at every timing, so
    // smear alone would have this trying timings for ever, and each try
    // opens the channel again: a black flash of a few frames. The drift is
    // exact, though: read at C samples a line, a picture that is really K a
    // line smears in a PATTERN that repeats every C/|C-K| samples (every 8th
    // or 9th for 640 against 720) and nowhere between. So the smear is
    // counted by where in that period it falls, in eighths, and the share
    // of it in the two worst eighths is the test. Measured:
    //   a real 720x400 text screen read at 640x400   0.88
    //   a picture scaled by a non-whole factor       0.28 to 0.43
    //   film grain, sharp game texture               0.25, 0.26 (even)
    // Another timing is tried only when the picture is not pixel-sharp AND
    // its smear has that timing's pattern; a timing that loses a trial is
    // left alone for a minute.
    //
    // Crisp is "pixel-sharp beyond question": a wrong clock has scored as
    // low as 0.08 on a sparse text screen, the right one 0.00.
    const double Crisp = 0.03, Patterned = 0.65;
    const int MinEdges = 2000, Bins = 8, MinPerBin = 60;
    const long LetAlone = 60_000;

    /// One look at a complete frame. True when the timing was changed and
    /// the channel must be opened again.
    bool Judge(IntPtr channel, Judgement j, VideoFrame f)
    {
        if (!j.Eligible.Contains(j.Current)) return false;
        bool settled = j.Best >= 0 && j.Best == j.Current;
        // A settled timing is watched on every third frame (a look is a
        // fraction of a millisecond, on the thread the frames arrive on).
        // One on trial is decided on three consecutive frames, after the
        // first two, which may straddle the change of timing: the card
        // answers a change within a frame or two.
        if (settled ? ++j.Watched % 3 != 0 : ++j.Skipped <= 2) return false;

        int here = j.Candidates[j.Current].HTotal;
        var others = j.Eligible.Where(i => i != j.Current).ToArray();
        var (score, edges, pattern) = Smear(f, here, others.Select(i => j.Candidates[i].HTotal).ToArray());
        if (edges < MinEdges) return false;           // a blank or flat screen says nothing
        j.Evidence.Add(score);
        for (int i = 0; i < others.Length; i++) j.Pattern[others[i]] += pattern[i];
        int looks = settled ? 5 : 3;
        if (j.Evidence.Count < looks) return false;
        double mean = j.Evidence.Average();
        long now = Environment.TickCount64;
        // The timings whose pattern the smear has, not refused and not just beaten.
        var suspects = others.Where(i => j.Pattern[i] / looks >= Patterned && j.Scores[i] != double.MaxValue
                                         && (j.LostAt[i] == 0 || now - j.LostAt[i] > LetAlone)).ToList();
        string patterns = string.Join(", ", others.Select(i => $"{j.Candidates[i].Spec} {j.Pattern[i] / looks:0.00}"));
        j.Forget();

        if (settled)
        {
            // SETTLED, and it stays unless the picture smears in another
            // timing's pattern for two looks running (ten frames watched).
            if (mean < Crisp || suspects.Count == 0) { j.Smeary = 0; return false; }
            if (++j.Smeary < 2) return false;
            j.Smeary = 0;
            Note($"{j.Candidates[j.Current].Spec} smears ({mean:0.00}) in the pattern of {string.Join(", ", suspects.Select(i => j.Candidates[i].Spec))}");
            j.Best = -1;
        }

        j.Scores[j.Current] = mean;
        Note($"{j.Candidates[j.Current].Spec} smear {mean:0.00} over {edges} edges; pattern of {patterns}");
        if (j.Home < 0)
        {
            // A ROUND BEGINS HERE (a sync seen for the first time, or a
            // settled timing gone wrong). With no suspect there is nothing to
            // try: this timing stands, without a flash.
            if (mean < Crisp || suspects.Count == 0) { Settle(j, j.Current, mean); return false; }
            j.Home = j.Current;
            j.ToTry.Clear();
            foreach (int i in suspects) j.ToTry.Enqueue(i);
            return SetTiming(channel, j, j.ToTry.Dequeue());
        }

        // ON TRIAL. Pixel-sharp wins outright; otherwise the next suspect,
        // and after the last, the verdict.
        int home = j.Home;
        if (mean >= Crisp && j.ToTry.Count > 0) return SetTiming(channel, j, j.ToTry.Dequeue());
        j.Home = -1;
        j.ToTry.Clear();
        var tried = j.Eligible.Where(i => j.Scores[i] is { } v && v != double.MaxValue).ToList();
        int best = tried.MinBy(i => j.Scores[i]!.Value);
        double b = j.Scores[best]!.Value, h = j.Scores[home] ?? double.MaxValue;
        // ONLY A CLEAR WINNER displaces the timing the round started from:
        // well under its smear, not a hair under it.
        int keep = best != home && b <= h * 0.6 && h - b >= 0.05 ? best : home;
        foreach (int i in tried) if (i != keep) j.LostAt[i] = now;
        Note(keep == home ? $"no clear winner over {j.Candidates[home].Spec} ({string.Join(", ", tried.Select(i => $"{j.Candidates[i].Spec} {j.Scores[i]:0.00}"))})"
                          : $"{j.Candidates[keep].Spec} ({b:0.00}) is clearly sharper than {j.Candidates[home].Spec} ({h:0.00})");
        Settle(j, keep, j.Scores[keep]!.Value);
        return keep != j.Current && SetTiming(channel, j, keep);
    }

    void Settle(Judgement j, int best, double score)
    {
        j.Best = best;
        j.BestScore = score;
        Note($"settled on {j.Candidates[best].Spec} (smear {score:0.00})");
        TimingText = $"{InputKind.ToString().ToUpperInvariant()} {j.Candidates[best]}, sync {j.Sync.Key}, automatic, {j.Eligible.Count} timings fit";
        Report(j.Sync.Key, j);
    }

    /// What the menu and vga_timing show: the sync, the timings that fit it and which is in use.
    void Report(string key, Judgement j) => Timings = new TimingReport(key, j.Manual,
        j.Candidates.Select((t, i) => new TimingChoice(t.Spec, t.ToString(), i == j.Current, j.Sync.Fits(t), j.Scores[i] == double.MaxValue,
            j.Scores[i] is { } s && s != double.MaxValue ? s : null)).ToList());

    /// A timing chosen by hand for the sync now coming in ("720x400",
    /// "720x400/900", or its number in the list), or null for automatic.
    /// Kept in the device's settings, per sync.
    public override string SetTiming(string? spec)
    {
        var report = Timings ?? throw new InvalidOperationException("no analog signal is locked on " + Device);
        if (spec == null)
        {
            var fewer = new Dictionary<string, string>(Settings.Timings);
            fewer.Remove(report.Sync);
            Settings.Timings = fewer;
            // Judged afresh, from the card's first choice.
            lock (_judgements) _judgements.Remove(report.Sync);
        }
        else
        {
            var choice = int.TryParse(spec, out int n) && n >= 1 && n <= report.Choices.Count ? report.Choices[n - 1]
                : report.Choices.FirstOrDefault(c => c.Spec == spec) ?? report.Choices.FirstOrDefault(c => c.Spec.StartsWith(spec + "/"))
                ?? throw new ArgumentException($"no timing \"{spec}\" fits this sync; the choices are {string.Join(", ", report.Choices.Select(c => c.Spec))}");
            // A new dictionary each time: a session reading it, or the settings
            // being saved, never sees it half changed.
            Settings.Timings = new Dictionary<string, string>(Settings.Timings) { [report.Sync] = choice.Spec };
            lock (_judgements) if (_judgements.TryGetValue(report.Sync, out var j)) { int i = report.Choices.IndexOf(choice); if (j.Scores[i] == double.MaxValue) j.Scores[i] = null; }
        }
        Bench.Save();
        Reopen();
        return report.Sync;
    }

    bool SetTiming(IntPtr channel, Judgement j, int i)
    {
        if (i < 0) return false;
        var t = j.Candidates[i];
        if (MW.MWSetVideoTiming(channel, ref t) != MW.Result.Succeeded) { j.Scores[i] = double.MaxValue; return false; }
        j.Asked = i;
        j.Skipped = 0;
        j.Forget();
        Note($"asked the card for {t.Spec}");
        return true;
    }

    /// The share of pixel edges whose middle sample lies between the colours
    /// either side of it, over rows spread down the frame; and, for each
    /// other number of samples a line, how much of that smear falls in one
    /// place in the period a clock of that rate would leave: the share of it
    /// in the two most smeared eighths of the period (0.25 when it is even;
    /// 0 where there are too few edges to say).
    static unsafe (double score, int edges, double[] pattern) Smear(VideoFrame f, int here, int[] others)
    {
        int edges = 0, smeared = 0;
        int step = Math.Max(1, f.Height / 150);
        // Where in each other clock's period every column falls.
        var bin = new byte[others.Length][];
        for (int k = 0; k < others.Length; k++)
        {
            bin[k] = new byte[f.Width];
            double per = Math.Abs(here - others[k]) / (double)Math.Max(1, here);
            for (int x = 0; x < f.Width; x++) bin[k][x] = (byte)((x * per % 1.0) * Bins);
        }
        var all = new int[others.Length, Bins];
        var soft = new int[others.Length, Bins];
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
                        bool s = b > lo + q && b < hi - q;
                        if (s) smeared++;
                        for (int k = 0; k < others.Length; k++)
                        {
                            int at = bin[k][x - 1];
                            all[k, at]++;
                            if (s) soft[k, at]++;
                        }
                    }
                    a = b; b = c;
                }
            }
        }
        var pattern = new double[others.Length];
        for (int k = 0; k < others.Length; k++)
        {
            double sum = 0, most = 0, next = 0;
            bool enough = true;
            for (int i = 0; i < Bins; i++)
            {
                if (all[k, i] < MinPerBin) { enough = false; break; }
                double rate = (double)soft[k, i] / all[k, i];
                sum += rate;
                if (rate > most) { next = most; most = rate; }
                else if (rate > next) next = rate;
            }
            pattern[k] = enough && sum > 0 ? (most + next) / sum : 0;
        }
        return (edges == 0 ? 0 : (double)smeared / edges, edges, pattern);
    }
}
