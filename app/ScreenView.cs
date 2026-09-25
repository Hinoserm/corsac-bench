// One capture device, live. The picture's shape changes whenever the machine
// changes video mode (720x400 text, 640x480, 1024x768...); these settings say
// what the view does about it.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace CorsacBench;

public enum ScreenAspect
{
    /// <summary>Every mode fills a 4:3 picture, as a CRT shows it: 720x400 text is stretched tall.</summary>
    Monitor,
    /// <summary>One captured pixel is one square pixel.</summary>
    Pixels,
    /// <summary>Fills the view whatever its shape.</summary>
    Stretch,
}

public enum ScreenScale
{
    /// <summary>As large as fits.</summary>
    Fit,
    /// <summary>The largest whole multiple that fits, for crisp text.</summary>
    Whole,
    /// <summary>One to one, however large the window.</summary>
    Actual,
}

public enum ShapeChange
{
    /// <summary>The window changes size to show the new picture at the chosen scale.</summary>
    ResizeWindow,
    /// <summary>The window stays; the picture refits inside it.</summary>
    KeepWindow,
}

public sealed class ScreenSettings
{
    /// <summary>0 x 0 follows the signal coming in; anything else is a fixed capture size.</summary>
    public int CaptureW { get; set; }
    public int CaptureH { get; set; }
    public ScreenAspect Aspect { get; set; } = ScreenAspect.Monitor;
    public ScreenScale Scale { get; set; } = ScreenScale.Fit;
    public bool Smooth { get; set; } = true;
    public bool TrimBorders { get; set; }
    public ShapeChange OnChange { get; set; } = ShapeChange.ResizeWindow;
    public int MaxFps { get; set; } = 30;
    public bool ShowInfo { get; set; } = true;
    /// <summary>For devices with a native low-latency source: how frames meet the display.</summary>
    public ScreenPresent Present { get; set; } = ScreenPresent.LowestLatency;
}

/// <summary>One screens window: which devices it shows, how, and where it was.</summary>
public sealed class ScreenWindowConfig
{
    public List<string> Devices { get; set; } = new();
    public string Layout { get; set; } = "grid";
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public int W { get; set; } = 1000;
    public int H { get; set; } = 800;
    public bool Open { get; set; } = true;
}

public sealed class ScreenView : Control
{
    public readonly string Device;
    public ScreenSettings S;
    Bitmap? _frame;
    readonly object _gate = new();
    long _seq;
    double _fps;
    int _nativeW, _nativeH;
    string _error = "";
    Rectangle _trim;                    // the part of the frame shown, after trimming borders
    Rectangle _trimSeen; int _trimAgree;
    Size _shape;                        // the last picture shape, to notice a change
    readonly List<string> _changes = new();
    volatile bool _running = true, _paused;
    readonly Thread? _pump;

    // THE CARD'S OWN PATH, for devices that have one (Magewell): the frames
    // come from a shared VideoSource and a Direct3D surface draws them as they
    // land. Every other device keeps the DirectShow pump above, as it was.
    readonly VideoSource? _native;
    readonly D3DSurface? _surface;
    readonly System.Windows.Forms.Timer? _barTimer;
    Size _frameSize;
    long _trimAt;

    /// <summary>Raised on the UI thread when the picture changes shape; carries the size it wants shown at.</summary>
    public event Action<ScreenView, Size>? ShapeChanged;
    public event Action<ScreenView>? Solo;
    /// <summary>The view asks for a window of its own.</summary>
    public event Action<ScreenView>? PopOut;

    public ScreenView(string device, ScreenSettings settings)
    {
        Device = device;
        S = settings;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
        ContextMenuStrip = new ContextMenuStrip();
        ContextMenuStrip.Opening += (_, _) => BuildMenu();
        try { _native = VideoSource.Use(device, settings); }
        catch (Exception e) { _error = e.Message; }
        if (_native != null)
        {
            _surface = new D3DSurface { ContextMenuStrip = ContextMenuStrip };
            _surface.DoubleClick += (_, _) => Solo?.Invoke(this);
            Controls.Add(_surface);
            _native.Frame += OnNative;
            _barTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _barTimer.Tick += (_, _) => { if (BarShown) Invalidate(BarArea); };
            _barTimer.Start();
        }
        else
        {
            _pump = new Thread(Pump) { IsBackground = true, Name = "screen " + device };
            _pump.Start();
        }
    }

    protected override void Dispose(bool disposing)
    {
        _running = false;
        if (disposing && _native != null)
        {
            _native.Frame -= OnNative;
            _barTimer!.Dispose();
            VideoSource.Release(_native, 2000);
        }
        base.Dispose(disposing);
    }

    public void Reopen()
    {
        if (_native != null) _native.Reopen();
        else
        {
            try { Vga.Open(Device, S.CaptureW, S.CaptureH); _error = ""; }
            catch (Exception e) { _error = e.Message; }
        }
        _shape = Size.Empty;
    }

    bool HasPicture => _frameSize != Size.Empty;

    /// A frame from the native source, on its capture thread. It is drawn
    /// by the surface without the UI thread; the UI hears only of a new
    /// shape or trim.
    void OnNative(VideoFrame f)
    {
        var size = new Size(f.Width, f.Height);
        Rectangle? trim = null;
        if (!S.TrimBorders) trim = new Rectangle(Point.Empty, size);
        else if (Environment.TickCount64 - _trimAt >= 250)
        {
            _trimAt = Environment.TickCount64;
            trim = FindPicture(f.Address, f.Stride, f.Width, f.Height, 4);
        }
        bool changed;
        lock (_gate)
        {
            var before = (_frameSize, _trim);
            _frameSize = size;
            _nativeW = _native!.NativeW; _nativeH = _native.NativeH; _fps = _native.Fps;
            if (trim != null) Settle(trim.Value);
            changed = before != (_frameSize, _trim);
        }
        if (changed && IsHandleCreated) BeginInvoke((Action)Changed);
    }

    /// The surface's place (the picture area) and what it draws there.
    void Place()
    {
        if (_surface == null) return;
        var area = PictureArea;
        if (_surface.Bounds != area) _surface.Bounds = area;
        Rectangle src, dst;
        lock (_gate)
        {
            src = _frameSize.IsEmpty ? Rectangle.Empty : _trim.IsEmpty ? new Rectangle(Point.Empty, _frameSize) : _trim;
            dst = src.IsEmpty ? Rectangle.Empty : Placement(src);
        }
        _surface.Show(_paused ? null : _native, src, dst, S.Smooth, S.Present);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Place();
    }

    void Pump()
    {
        Reopen();
        while (_running)
        {
            var started = DateTime.UtcNow;
            if (_paused || !Visible && IsHandleCreated) { Thread.Sleep(200); continue; }
            try
            {
                var f = Vga.Frame(Device, _seq, 0.25);
                if (f.Seq != _seq)
                {
                    var trim = S.TrimBorders ? FindPicture(f.Picture) : new Rectangle(Point.Empty, f.Picture.Size);
                    lock (_gate)
                    {
                        _frame?.Dispose();
                        _frame = f.Picture;
                        _seq = f.Seq; _fps = f.Fps; _nativeW = f.NativeW; _nativeH = f.NativeH;
                        _frameSize = f.Picture.Size;
                        Settle(trim);
                    }
                    _error = "";
                    if (IsHandleCreated) BeginInvoke((Action)Changed);
                }
                else f.Dispose();
            }
            catch (Exception e)
            {
                _error = e.Message;
                if (IsHandleCreated) BeginInvoke(() => Invalidate());
                Thread.Sleep(1000);
            }
            var left = TimeSpan.FromSeconds(1.0 / Math.Max(1, S.MaxFps)) - (DateTime.UtcNow - started);
            if (left > TimeSpan.Zero) Thread.Sleep(left);
        }
    }

    /// <summary>A trim takes effect once three frames agree on it, so a dark scene does not make the picture jump.</summary>
    void Settle(Rectangle trim)
    {
        if (trim == _trimSeen) _trimAgree++;
        else { _trimSeen = trim; _trimAgree = 1; }
        if (_trim.IsEmpty || _trimAgree >= 3 || _trim.Right > _frameSize.Width || _trim.Bottom > _frameSize.Height)
            _trim = _trimSeen;
    }

    /// <summary>
    /// The picture inside black borders the card added: only borders that are
    /// even on both sides (letterbox or pillarbox), so a black screen with
    /// text in one corner is left alone.
    /// </summary>
    static Rectangle FindPicture(Bitmap b)
    {
        var bits = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try { return FindPicture(bits.Scan0, bits.Stride, b.Width, b.Height, 3); }
        finally { b.UnlockBits(bits); }
    }

    static Rectangle FindPicture(IntPtr scan0, int stride, int w, int h, int bytes)
    {
        {
            unsafe
            {
                byte* p0 = (byte*)scan0;
                bool Lit(int x, int y) { byte* p = p0 + y * stride + x * bytes; return p[0] + p[1] + p[2] > 48; }
                bool RowLit(int y) { for (int x = 0; x < w; x += 2) if (Lit(x, y)) return true; return false; }
                bool ColLit(int x) { for (int y = 0; y < h; y += 2) if (Lit(x, y)) return true; return false; }
                int top = 0; while (top < h / 3 && !RowLit(top)) top++;
                int bottom = h - 1; while (bottom > h * 2 / 3 && !RowLit(bottom)) bottom--;
                int left = 0; while (left < w / 3 && !ColLit(left)) left++;
                int right = w - 1; while (right > w * 2 / 3 && !ColLit(right)) right--;
                if (Math.Abs(top - (h - 1 - bottom)) > 2) { top = 0; bottom = h - 1; }
                if (Math.Abs(left - (w - 1 - right)) > 2) { left = 0; right = w - 1; }
                return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            }
        }
    }

    /// <summary>The size the picture is meant to be seen at, before any window scaling.</summary>
    Size Shape(Rectangle src) => S.Aspect == ScreenAspect.Monitor
        ? new Size(Math.Max(src.Width, src.Height * 4 / 3), Math.Max(src.Width, src.Height * 4 / 3) * 3 / 4)
        : src.Size;

    void Changed()
    {
        Size shape;
        lock (_gate) shape = !HasPicture ? Size.Empty : new Size(_trim.Width, _trim.Height);
        if (!shape.IsEmpty && shape != _shape)
        {
            if (!_shape.IsEmpty)
            {
                _changes.Add($"{DateTime.Now:HH:mm:ss} {_shape.Width}x{_shape.Height} -> {shape.Width}x{shape.Height}");
                if (_changes.Count > 20) _changes.RemoveAt(0);
            }
            _shape = shape;
            if (S.OnChange == ShapeChange.ResizeWindow) ShapeChanged?.Invoke(this, Wanted(Screen.FromControl(this).WorkingArea.Size));
        }
        Place();
        Invalidate();
    }

    /// <summary>The view size that shows the picture at the chosen scale, within `room`.</summary>
    public Size Wanted(Size room)
    {
        Rectangle src;
        lock (_gate) src = !HasPicture ? new Rectangle(0, 0, 720, 400) : _trim;
        var shape = Shape(src);
        int bar = BarHeight;
        room = new Size(room.Width - 40, room.Height - 120 - bar);
        Size picture;
        switch (S.Scale)
        {
            case ScreenScale.Actual:
                picture = shape;
                break;
            case ScreenScale.Whole:
            {
                int k = Math.Max(1, Math.Min(room.Width / shape.Width, room.Height / shape.Height));
                picture = new Size(shape.Width * k, shape.Height * k);
                break;
            }
            default:
            {
                // Keep the picture's present height where it fits; the width follows the shape.
                int hgt = Math.Min(Math.Max(PictureArea.Height, 240), room.Height);
                int wid = (int)Math.Round((double)hgt * shape.Width / shape.Height);
                if (wid > room.Width) { wid = room.Width; hgt = (int)Math.Round((double)wid * shape.Height / shape.Width); }
                picture = new Size(wid, hgt);
                break;
            }
        }
        // And the bar under it, so a window sized to the picture still shows all of it.
        return new Size(picture.Width, picture.Height + bar);
    }

    // THE PICTURE AND ITS INFORMATION NEVER SHARE A PIXEL. The information is
    // a bar of its own under the picture; the picture is placed in what is
    // left. Nothing is ever drawn over the machine's screen.

    static Font BarFont => SystemFonts.StatusFont ?? SystemFonts.DefaultFont;

    /// Whether the bar is shown: when asked for, or when there is something
    /// the person must see (an error, or no picture yet).
    bool BarShown => S.ShowInfo || Error != "" || !HasPicture;

    string Error => _error != "" ? _error : _native?.Error is { Length: > 0 } e ? e : _surface?.Error ?? "";

    int BarHeight => BarShown ? TextRenderer.MeasureText("Ag", BarFont).Height + 6 : 0;

    /// Where the picture may go: the view less the bar.
    Rectangle PictureArea => new(0, 0, ClientSize.Width, Math.Max(1, ClientSize.Height - BarHeight));

    Rectangle BarArea => new(0, PictureArea.Bottom, ClientSize.Width, ClientSize.Height - PictureArea.Bottom);

    Rectangle Placement(Rectangle src)
    {
        var shape = Shape(src);
        var room = PictureArea;
        if (S.Aspect == ScreenAspect.Stretch) return room;
        double k = S.Scale switch
        {
            ScreenScale.Actual => 1,
            ScreenScale.Whole => Math.Max(1, Math.Floor(Math.Min((double)room.Width / shape.Width, (double)room.Height / shape.Height))),
            _ => Math.Min((double)room.Width / shape.Width, (double)room.Height / shape.Height),
        };
        int w = (int)Math.Round(shape.Width * k), h = (int)Math.Round(shape.Height * k);
        return new Rectangle(room.X + (room.Width - w) / 2, room.Y + (room.Height - h) / 2, w, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        string info;
        lock (_gate)
        {
            if (_native != null && HasPicture)
            {
                var src = _trim.IsEmpty ? new Rectangle(Point.Empty, _frameSize) : _trim;
                var n = _native;
                info = $"{Short(Device)}  {src.Width}x{src.Height}" +
                       (src.Size != _frameSize ? $" of {_frameSize.Width}x{_frameSize.Height}" : "") +
                       (S.CaptureW == 0 ? "" : $" (fixed; signal {_nativeW}x{_nativeH})") +
                       $"  {n.SignalHz:0.##} Hz{(n.Interlaced ? " interlaced" : "")}" +
                       $"  {n.Fps:0} fps" +
                       (n.CaptureLatencyMs >= 0 ? $"  card {n.CaptureLatencyMs:0.0} ms" : "") +
                       (_surface!.PresentMs >= 0 ? $" + display {_surface.PresentMs:0.0} ms" : "") +
                       (S.Present == ScreenPresent.LowestLatency && _surface.TearingSupported ? "  tearing allowed" : "  synchronised") +
                       (_paused ? "  PAUSED" : "");
            }
            else if (_frame != null)
            {
                var src = _trim.IsEmpty ? new Rectangle(Point.Empty, _frame.Size) : _trim;
                var dst = Placement(src);
                g.InterpolationMode = S.Smooth ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(_frame, dst, src, GraphicsUnit.Pixel);
                info = $"{Short(Device)}  {src.Width}x{src.Height}" +
                       (src.Size != _frame.Size ? $" of {_frame.Width}x{_frame.Height}" : "") +
                       (S.CaptureW == 0 ? " (signal)" : $" (fixed; signal {_nativeW}x{_nativeH})") +
                       $"  {_fps:0} fps{(_paused ? "  PAUSED" : "")}";
            }
            else info = $"{Short(Device)}  waiting for a picture";
        }
        if (_changes.Count > 0 && S.ShowInfo) info += "   last change " + _changes[^1];
        if (Error != "") info += "   " + Error;
        if (BarShown)
        {
            var bar = BarArea;
            using var b = new SolidBrush(Color.FromArgb(32, 32, 36));
            g.FillRectangle(b, bar);
            TextRenderer.DrawText(g, info, BarFont, new Rectangle(bar.X + 6, bar.Y, bar.Width - 12, bar.Height),
                Error != "" ? Color.Orange : Color.Gainsboro,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    static string Short(string device) => device.StartsWith("Video (") && device.EndsWith(")") ? device[7..^1] : device;

    protected override void OnDoubleClick(EventArgs e) { Solo?.Invoke(this); base.OnDoubleClick(e); }

    public Bitmap? Snapshot()
    {
        lock (_gate)
        {
            if (_native != null)
            {
                var f = _native.Latest;
                if (f == null) return null;
                var r = _trim.IsEmpty || _trim.Right > f.Width || _trim.Bottom > f.Height ? new Rectangle(0, 0, f.Width, f.Height) : _trim;
                return f.ToBitmap(r);
            }
            if (_frame == null) return null;
            return _frame.Clone(_trim.IsEmpty ? new Rectangle(Point.Empty, _frame.Size) : _trim, PixelFormat.Format24bppRgb);
        }
    }

    /// <summary>A setting changed: save it and show the picture afresh.</summary>
    void Apply(Action a)
    {
        a();
        Bench.Save();
        lock (_gate) _trim = !HasPicture ? Rectangle.Empty : new Rectangle(Point.Empty, _frameSize);
        _shape = Size.Empty;
        Changed();
    }

    void BuildMenu()
    {
        var m = ContextMenuStrip!;
        m.Items.Clear();
        ToolStripMenuItem Item(string text, bool check, Action a) => new(text, null, (_, _) => Apply(a)) { Checked = check };
        ToolStripMenuItem Sub(string text, params ToolStripItem[] items) { var s = new ToolStripMenuItem(text); s.DropDownItems.AddRange(items); return s; }

        var sizes = new List<ToolStripItem> { Item("Follow the signal", S.CaptureW == 0, () => { S.CaptureW = S.CaptureH = 0; Reopen(); }), new ToolStripSeparator() };
        try
        {
            IReadOnlyList<(int w, int h)> offered;
            if (_native != null) offered = _native.Sizes;
            else { Vga.Native(Device, out var l); offered = l; }
            foreach (var (w, h) in offered.OrderBy(s => s.w * s.h))
                sizes.Add(Item($"{w} x {h}", S.CaptureW == w && S.CaptureH == h, () => { S.CaptureW = w; S.CaptureH = h; Reopen(); }));
        }
        catch (Exception e) { sizes.Add(new ToolStripMenuItem(e.Message) { Enabled = false }); }

        m.Items.Add(Sub("Capture size", sizes.ToArray()));
        m.Items.Add(Sub("Shape",
            Item("Like a monitor (every mode 4:3)", S.Aspect == ScreenAspect.Monitor, () => S.Aspect = ScreenAspect.Monitor),
            Item("Square pixels", S.Aspect == ScreenAspect.Pixels, () => S.Aspect = ScreenAspect.Pixels),
            Item("Stretch to the window", S.Aspect == ScreenAspect.Stretch, () => S.Aspect = ScreenAspect.Stretch)));
        m.Items.Add(Sub("Scale",
            Item("Fit the window", S.Scale == ScreenScale.Fit, () => S.Scale = ScreenScale.Fit),
            Item("Whole multiples (crisp)", S.Scale == ScreenScale.Whole, () => S.Scale = ScreenScale.Whole),
            Item("Actual size", S.Scale == ScreenScale.Actual, () => S.Scale = ScreenScale.Actual)));
        m.Items.Add(Sub("When the mode changes",
            Item("Resize the window to the new picture", S.OnChange == ShapeChange.ResizeWindow, () => S.OnChange = ShapeChange.ResizeWindow),
            Item("Keep the window; refit the picture", S.OnChange == ShapeChange.KeepWindow, () => S.OnChange = ShapeChange.KeepWindow)));
        if (_native != null)
            m.Items.Add(Sub("Presentation",
                Item("Lowest latency (tearing allowed)", S.Present == ScreenPresent.LowestLatency, () => S.Present = ScreenPresent.LowestLatency),
                Item("Synchronised to the display", S.Present == ScreenPresent.Synchronised, () => S.Present = ScreenPresent.Synchronised)));
        else
            m.Items.Add(Sub("Frame rate", new[] { 5, 10, 15, 30, 60 }.Select(f => (ToolStripItem)Item($"{f} fps", S.MaxFps == f, () => S.MaxFps = f)).ToArray()));
        m.Items.Add(Item("Smooth scaling", S.Smooth, () => S.Smooth = !S.Smooth));
        m.Items.Add(Item("Trim black borders", S.TrimBorders, () => S.TrimBorders = !S.TrimBorders));
        m.Items.Add(Item("Show information", S.ShowInfo, () => S.ShowInfo = !S.ShowInfo));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Open in a new window", null, (_, _) => PopOut?.Invoke(this)));
        m.Items.Add(new ToolStripMenuItem(_paused ? "Resume" : "Pause", null, (_, _) => { _paused = !_paused; Place(); Invalidate(); }));
        m.Items.Add(new ToolStripMenuItem("Copy picture", null, (_, _) => { using var b = Snapshot(); if (b != null) Clipboard.SetImage(b); }));
        m.Items.Add(new ToolStripMenuItem("Save picture...", null, (_, _) => SaveAs()));
        m.Items.Add(new ToolStripMenuItem("Use for vga_capture", null, (_, _) => { Bench.DefaultVga = Device; Bench.Save(); }) { Checked = Bench.DefaultVga == Device });
        if (_changes.Count > 0)
            m.Items.Add(Sub("Mode changes", _changes.AsEnumerable().Reverse().Select(c => (ToolStripItem)new ToolStripMenuItem(c) { Enabled = false }).ToArray()));
    }

    void SaveAs()
    {
        using var b = Snapshot();
        if (b == null) return;
        using var d = new SaveFileDialog { Filter = "PNG|*.png", InitialDirectory = Bench.Dir, FileName = $"{Short(Device)} {DateTime.Now:yyyy-MM-dd HHmmss}.png" };
        if (d.ShowDialog(this) == DialogResult.OK) b.Save(d.FileName, ImageFormat.Png);
    }
}
