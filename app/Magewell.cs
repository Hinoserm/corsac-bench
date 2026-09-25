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

static class MW
{
    const string Dll = "LibMWCapture.dll";

    public const ulong NotifyVideoSignalChange = 0x0020;       // MWCAP_NOTIFY_VIDEO_SIGNAL_CHANGE
    public const ulong NotifyVideoFrameBuffering = 0x0100;     // MWCAP_NOTIFY_VIDEO_FRAME_BUFFERING
    public const ulong NotifyVideoFrameBuffered = 0x0400;      // MWCAP_NOTIFY_VIDEO_FRAME_BUFFERED
    public const uint FourccBgra = 'B' | ('G' << 8) | ('R' << 16) | ((uint)'A' << 24);

    public enum Result { Succeeded = 0, Failed, InvalidParams }
    public enum SignalState { None = 0, Unsupported, Locking, Locked }

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
            try { Session(); }
            catch (Exception e) { Error = e.Message; }
            if (_run) Thread.Sleep(500);
        }
    }

    /// One open of the channel, until it must be opened again (a new size,
    /// a signal change, a stop). The loop is the SDK example's.
    void Session()
    {
        _reopen = false;
        IntPtr channel = MW.MWOpenChannelByPath(_path);
        if (channel == IntPtr.Zero) throw new InvalidOperationException("could not open the Magewell channel");
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
            notify = MW.MWRegisterNotify(channel, notifyEvent, MW.NotifyVideoFrameBuffering | MW.NotifyVideoSignalChange);
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
}
