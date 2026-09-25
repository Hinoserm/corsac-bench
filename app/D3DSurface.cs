// The picture from a native capture source, drawn by Direct3D 11 on a
// thread of its own the moment each frame is complete: a flip-model swap
// chain allowed at most one frame queued, the frame copied straight from the
// capture buffer into a texture, and presented without waiting for the
// display's refresh (tearing allowed) unless asked to synchronise. Pixel
// values go through untouched: an 8-bit BGRA texture onto an 8-bit BGRA
// back buffer, no colour conversion, point sampling unless smoothing is on.

using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace CorsacBench;

public enum ScreenPresent
{
    /// <summary>Each frame is shown the moment it is complete, even partway through the display's refresh (it can tear).</summary>
    LowestLatency,
    /// <summary>Each frame waits for the display's next refresh: no tearing, up to one refresh more latency.</summary>
    Synchronised,
}

public sealed class D3DSurface : Control
{
    const string Shaders = @"
cbuffer Source : register(b0) { float4 src; };
Texture2D picture : register(t0);
SamplerState sampling : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V vs(uint id : SV_VertexID)
{
    V v;
    v.uv = float2((id << 1) & 2, id & 2);
    v.pos = float4(v.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return v;
}
float4 ps(V v) : SV_Target
{
    return float4(picture.Sample(sampling, src.xy + v.uv * src.zw).rgb, 1);
}";

    readonly object _gate = new();
    VideoSource? _source;
    Rectangle _src, _dst;
    bool _smooth;
    ScreenPresent _present;
    readonly AutoResetEvent _kick = new(false);
    Thread? _thread;
    volatile bool _run;

    /// Milliseconds from a frame being complete in memory to it being handed to the display.
    public double PresentMs { get; private set; } = -1;
    public double ShownFps { get; private set; }
    public string Error { get; private set; } = "";
    /// Whether the system lets this window tear (independent of the setting).
    public bool TearingSupported { get; private set; }

    public D3DSurface()
    {
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        BackColor = System.Drawing.Color.Black;
    }

    /// What to draw: the part `src` of the source's frames, into `dst` of this control.
    public void Show(VideoSource? source, Rectangle src, Rectangle dst, bool smooth, ScreenPresent present)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(source, _source))
            {
                if (_source != null) _source.Rows -= OnFrame;
                _source = source;
                if (_source != null) _source.Rows += OnFrame;
            }
            _src = src; _dst = dst; _smooth = smooth; _present = present;
        }
        _kick.Set();
    }

    void OnFrame(VideoFrame f) => _kick.Set();

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _run = true;
        var hwnd = Handle;
        _thread = new Thread(() => Render(hwnd)) { IsBackground = true, Name = "present", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _run = false;
        _kick.Set();
        _thread?.Join(2000);
        _thread = null;
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (_gate) { if (_source != null) _source.Rows -= OnFrame; _source = null; }
        base.Dispose(disposing);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) => _kick.Set();
    protected override void OnResize(EventArgs e) { base.OnResize(e); _kick.Set(); }

    [DllImport("kernel32.dll")] static extern uint WaitForSingleObjectEx(IntPtr handle, uint ms, bool alertable);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RawRect r);
    [StructLayout(LayoutKind.Sequential)] struct RawRect { public int Left, Top, Right, Bottom; }

    void Render(IntPtr hwnd)
    {
        while (_run)
        {
            try
            {
                Session(hwnd);
                Error = "";
            }
            catch (Exception e)
            {
                // A lost device (driver reset, adapter change): built afresh.
                Error = "display: " + e.Message;
                Thread.Sleep(500);
            }
        }
    }

    void Session(IntPtr hwnd)
    {
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            out ID3D11Device? device, out ID3D11DeviceContext? ctx).CheckError();
        using var _d = device!;
        using var _c = ctx!;
        using var dxgiDevice = device!.QueryInterface<IDXGIDevice1>();
        dxgiDevice.MaximumFrameLatency = 1;
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        bool tearing = false;
        using (var f5 = factory.QueryInterfaceOrNull<IDXGIFactory5>()) tearing = f5?.PresentAllowTearing ?? false;
        TearingSupported = tearing;

        var flags = SwapChainFlags.FrameLatencyWaitableObject | (tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None);
        var (cw, ch) = ClientPixels(hwnd);
        var desc = new SwapChainDescription1
        {
            Width = (uint)cw, Height = (uint)ch,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.None,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = flags,
        };
        using var chain1 = factory.CreateSwapChainForHwnd(device, hwnd, desc);
        using var chain = chain1.QueryInterface<IDXGISwapChain2>();
        chain.MaximumFrameLatency = 1;
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
        IntPtr waitable = chain.FrameLatencyWaitableObject;

        using var vs = device.CreateVertexShader(Compiler.Compile(Shaders, "vs", "screen.hlsl", "vs_4_0").Span);
        using var ps = device.CreatePixelShader(Compiler.Compile(Shaders, "ps", "screen.hlsl", "ps_4_0").Span);
        using var cb = device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Default));
        using var point = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
        using var linear = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));

        ID3D11RenderTargetView? target = null;
        ID3D11Texture2D? texture = null;
        ID3D11ShaderResourceView? view = null;
        long shownSeq = 0, count = 0, countAt = 0;
        int shownRows = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            while (_run)
            {
                // AT MOST ONE FRAME QUEUED: wait until the display has taken
                // the last one, then for a new frame (or a change of layout).
                WaitForSingleObjectEx(waitable, 1000, true);
                _kick.WaitOne(250);
                if (!_run) break;

                VideoSource? source; Rectangle src, dst; bool smooth; ScreenPresent present;
                lock (_gate) { source = _source; src = _src; dst = _dst; smooth = _smooth; present = _present; }

                var (w, h) = ClientPixels(hwnd);
                if (w != (int)desc.Width || h != (int)desc.Height || target == null)
                {
                    ctx!.ClearState();
                    target?.Dispose(); target = null;
                    if (w != (int)desc.Width || h != (int)desc.Height)
                    {
                        chain.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, flags).CheckError();
                        desc.Width = (uint)w; desc.Height = (uint)h;
                    }
                    using var back = chain.GetBuffer<ID3D11Texture2D>(0);
                    target = device.CreateRenderTargetView(back);
                }

                // ONLY THE ROWS THAT ARE NEW: the rest of the texture keeps
                // what it had, the frame before's lower part until this
                // frame's lines replace it, as a CRT's beam would.
                var frame = source?.Current;
                bool completed = false;
                if (frame != null)
                {
                    long seq = frame.Seq;
                    int rows = frame.Rows;
                    if (texture == null || texture.Description.Width != frame.Width || texture.Description.Height != frame.Height)
                    {
                        view?.Dispose(); texture?.Dispose();
                        texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)frame.Width, (uint)frame.Height, 1, 1, BindFlags.ShaderResource));
                        view = device.CreateShaderResourceView(texture);
                        shownSeq = 0;
                    }
                    int from = seq == shownSeq ? shownRows : 0;
                    if (rows > from)
                    {
                        ctx!.UpdateSubresource(new ReadOnlySpan<byte>(frame.Pixels, from * frame.Stride, (rows - from) * frame.Stride), texture, 0,
                            (uint)frame.Stride, 0, new Box(0, from, 0, frame.Width, rows, 1));
                        completed = rows == frame.Height && (seq != shownSeq || shownRows < rows);
                        shownSeq = seq; shownRows = rows;
                    }
                }

                ctx!.ClearRenderTargetView(target, new Color4(0, 0, 0, 1));
                if (texture != null && view != null && dst.Width > 0 && dst.Height > 0)
                {
                    float tw = texture.Description.Width, th = texture.Description.Height;
                    if (src.IsEmpty) src = new Rectangle(0, 0, (int)tw, (int)th);
                    ctx.UpdateSubresource(new[] { src.X / tw, src.Y / th, src.Width / tw, src.Height / th }, cb);
                    ctx.OMSetRenderTargets(target);
                    ctx.RSSetViewport(new Viewport(dst.X, dst.Y, dst.Width, dst.Height));
                    ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    ctx.VSSetShader(vs);
                    ctx.PSSetShader(ps);
                    ctx.PSSetConstantBuffer(0, cb);
                    ctx.PSSetShaderResource(0, view);
                    ctx.PSSetSampler(0, smooth ? linear : point);
                    ctx.Draw(3, 0);
                }

                bool tear = tearing && present == ScreenPresent.LowestLatency;
                chain.Present(tear ? 0u : 1u, tear ? PresentFlags.AllowTearing : PresentFlags.None).CheckError();
                if (completed)
                {
                    PresentMs = (Stopwatch.GetTimestamp() - frame!.Completed) * 1000.0 / Stopwatch.Frequency;
                    count++;
                }
                if (clock.ElapsedMilliseconds >= 1000)
                {
                    ShownFps = (count - countAt) * 1000.0 / clock.ElapsedMilliseconds;
                    countAt = count;
                    clock.Restart();
                }
            }
        }
        finally
        {
            ctx?.ClearState();
            view?.Dispose(); texture?.Dispose(); target?.Dispose();
        }
    }

    static (int w, int h) ClientPixels(IntPtr hwnd)
    {
        GetClientRect(hwnd, out var r);
        return (Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
    }
}
