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
    public bool Shown { get; set; }
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
    readonly Thread _pump;

    /// <summary>Raised on the UI thread when the picture changes shape; carries the size it wants shown at.</summary>
    public event Action<ScreenView, Size>? ShapeChanged;
    public event Action<ScreenView>? Solo;

    public ScreenView(string device, ScreenSettings settings)
    {
        Device = device;
        S = settings;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
        ContextMenuStrip = new ContextMenuStrip();
        ContextMenuStrip.Opening += (_, _) => BuildMenu();
        _pump = new Thread(Pump) { IsBackground = true, Name = "screen " + device };
        _pump.Start();
    }

    protected override void Dispose(bool disposing)
    {
        _running = false;
        base.Dispose(disposing);
    }

    public void Reopen()
    {
        try { Vga.Open(Device, S.CaptureW, S.CaptureH); _error = ""; }
        catch (Exception e) { _error = e.Message; }
        _shape = Size.Empty;
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
                var f = Vga.Frame(Device, _seq, 1.0);
                if (f.Seq != _seq)
                {
                    var trim = S.TrimBorders ? FindPicture(f.Picture) : new Rectangle(Point.Empty, f.Picture.Size);
                    lock (_gate)
                    {
                        _frame?.Dispose();
                        _frame = f.Picture;
                        _seq = f.Seq; _fps = f.Fps; _nativeW = f.NativeW; _nativeH = f.NativeH;
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
        if (_trim.IsEmpty || _trimAgree >= 3 || _trim.Width > _frame!.Width || _trim.Height > _frame.Height)
            _trim = _trimSeen;
    }

    /// <summary>
    /// The picture inside black borders the card added: only borders that are
    /// even on both sides (letterbox or pillarbox), so a black screen with
    /// text in one corner is left alone.
    /// </summary>
    static Rectangle FindPicture(Bitmap b)
    {
        int w = b.Width, h = b.Height;
        var bits = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                byte* p0 = (byte*)bits.Scan0;
                bool Lit(int x, int y) { byte* p = p0 + y * bits.Stride + x * 3; return p[0] + p[1] + p[2] > 48; }
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
        finally { b.UnlockBits(bits); }
    }

    /// <summary>The size the picture is meant to be seen at, before any window scaling.</summary>
    Size Shape(Rectangle src) => S.Aspect == ScreenAspect.Monitor
        ? new Size(Math.Max(src.Width, src.Height * 4 / 3), Math.Max(src.Width, src.Height * 4 / 3) * 3 / 4)
        : src.Size;

    void Changed()
    {
        Size shape;
        lock (_gate) shape = _frame == null ? Size.Empty : new Size(_trim.Width, _trim.Height);
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
        Invalidate();
    }

    /// <summary>The view size that shows the picture at the chosen scale, within `room`.</summary>
    public Size Wanted(Size room)
    {
        Rectangle src;
        lock (_gate) src = _frame == null ? new Rectangle(0, 0, 720, 400) : _trim;
        var shape = Shape(src);
        room = new Size(room.Width - 40, room.Height - 120);
        switch (S.Scale)
        {
            case ScreenScale.Actual:
                return shape;
            case ScreenScale.Whole:
            {
                int k = Math.Max(1, Math.Min(room.Width / shape.Width, room.Height / shape.Height));
                return new Size(shape.Width * k, shape.Height * k);
            }
            default:
            {
                // Keep the view's present height where it fits; the width follows the shape.
                int hgt = Math.Min(Math.Max(Height, 240), room.Height);
                int wid = (int)Math.Round((double)hgt * shape.Width / shape.Height);
                if (wid > room.Width) { wid = room.Width; hgt = (int)Math.Round((double)wid * shape.Height / shape.Width); }
                return new Size(wid, hgt);
            }
        }
    }

    Rectangle Placement(Rectangle src)
    {
        var shape = Shape(src);
        var room = ClientRectangle;
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
            if (_frame != null)
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
        if (_error != "") info += "\n" + _error;
        if (S.ShowInfo || _error != "" || _frame == null)
        {
            if (_changes.Count > 0 && S.ShowInfo) info += "\nlast change " + _changes[^1];
            var font = SystemFonts.StatusFont ?? SystemFonts.DefaultFont;
            var size = TextRenderer.MeasureText(info, font);
            using var b = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            g.FillRectangle(b, 4, 4, size.Width + 8, size.Height + 6);
            TextRenderer.DrawText(g, info, font, new Point(8, 7), _error != "" ? Color.Orange : Color.White);
        }
    }

    static string Short(string device) => device.StartsWith("Video (") && device.EndsWith(")") ? device[7..^1] : device;

    protected override void OnDoubleClick(EventArgs e) { Solo?.Invoke(this); base.OnDoubleClick(e); }

    public Bitmap? Snapshot()
    {
        lock (_gate)
        {
            if (_frame == null) return null;
            return _frame.Clone(_trim.IsEmpty ? new Rectangle(Point.Empty, _frame.Size) : _trim, PixelFormat.Format24bppRgb);
        }
    }

    /// <summary>A setting changed: save it and show the picture afresh.</summary>
    void Apply(Action a)
    {
        a();
        Bench.Save();
        lock (_gate) _trim = _frame == null ? Rectangle.Empty : new Rectangle(Point.Empty, _frame.Size);
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
            Vga.Native(Device, out var offered);
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
        m.Items.Add(Sub("Frame rate", new[] { 5, 10, 15, 30, 60 }.Select(f => (ToolStripItem)Item($"{f} fps", S.MaxFps == f, () => S.MaxFps = f)).ToArray()));
        m.Items.Add(Item("Smooth scaling", S.Smooth, () => S.Smooth = !S.Smooth));
        m.Items.Add(Item("Trim black borders", S.TrimBorders, () => S.TrimBorders = !S.TrimBorders));
        m.Items.Add(Item("Show information", S.ShowInfo, () => S.ShowInfo = !S.ShowInfo));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem(_paused ? "Resume" : "Pause", null, (_, _) => { _paused = !_paused; Invalidate(); }));
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
