// vga_capture_series: frames taken evenly over a stretch of time, laid out
// in one image in the order they were taken, to see what a screen did (a
// boot, a mode change, a hang) in a single look. Each frame's time is in a
// strip of its own beneath it, never over the picture.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json.Nodes;

namespace CorsacBench;

public static class Series
{
    /// The largest image a session is shown whole: Claude Code scales anything
    /// with a longer side down to this before the model sees it.
    const int Largest = 2000;
    const int Gap = 4;
    /// Base64 PNGs past this are sent as JPEG instead, under the 5 MB an image may be.
    const int MostBytes = 3_500_000;

    static long _made;
    const int Kept = 20;

    /// A file of its own for each series (several may run at once), in
    /// series/ under the bench folder; only the newest Kept are kept.
    public static string NewPath(string dir)
    {
        var folder = Path.Combine(dir, "series");
        Directory.CreateDirectory(folder);
        var old = new DirectoryInfo(folder).GetFiles("vga-series-*").OrderByDescending(f => f.LastWriteTimeUtc).Skip(Kept - 1);
        foreach (var f in old) { try { f.Delete(); } catch { } }
        return Path.Combine(folder, $"vga-series-{DateTime.Now:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref _made)}.png");
    }

    sealed record Shot(Bitmap? Picture, double At, DateTime Clock, int NativeW, int NativeH, string Note);

    public static JsonObject Capture(string device, int count, double seconds, string path)
    {
        var shots = Take(device, count, seconds);
        try
        {
            using var sheet = Lay(shots);
            byte[] bytes = Encode(sheet, out string mime);
            File.WriteAllBytes(Path.ChangeExtension(path, mime == "image/png" ? ".png" : ".jpg"), bytes);

            var text = new StringBuilder($"{device}: {count} frame{(count == 1 ? "" : "s")} over {seconds:0.###} s, " +
                $"{(count > 1 ? $"one every {seconds / (count - 1):0.###} s" : "one")}; left to right, top to bottom; " +
                $"{sheet.Width}x{sheet.Height}, saved to {Path.ChangeExtension(path, mime == "image/png" ? ".png" : ".jpg")}\n");
            for (int i = 0; i < shots.Count; i++)
            {
                var s = shots[i];
                text.Append($"  {i + 1}: +{s.At:0.000} s  {s.Clock:HH:mm:ss.fff}  ");
                text.Append(s.Picture != null ? $"{s.Picture.Width}x{s.Picture.Height}" : "no picture");
                if (s.Note != "") text.Append("  " + s.Note);
                text.Append('\n');
            }
            return new JsonObject
            {
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = text.ToString().TrimEnd() },
                    new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(bytes), ["mimeType"] = mime }),
            };
        }
        finally { foreach (var s in shots) s.Picture?.Dispose(); }
    }

    /// The frames: the first at once, the last `seconds` later, the rest
    /// evenly between. Each is the first frame complete at or after its time.
    static List<Shot> Take(string device, int count, double seconds)
    {
        var shots = new List<Shot>();
        var clock = Stopwatch.StartNew();
        var native = VideoSource.Use(device);
        try
        {
            long seen = 0;
            for (int i = 0; i < count; i++)
            {
                double due = count == 1 ? 0 : seconds * i / (count - 1);
                var wait = TimeSpan.FromSeconds(due) - clock.Elapsed;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                try
                {
                    if (native != null)
                    {
                        var f = native.Next(native.Seq, 2000);
                        double at = clock.Elapsed.TotalSeconds;
                        shots.Add(f != null
                            ? new Shot(f.ToBitmap(), at, DateTime.Now, native.NativeW, native.NativeH, native.TimingText)
                            : new Shot(null, at, DateTime.Now, 0, 0, native.Error != "" ? native.Error : "no frame"));
                    }
                    else
                    {
                        using var f = Vga.Frame(device, seen, 2);
                        seen = f.Seq;
                        shots.Add(new Shot(new Bitmap(f.Picture), clock.Elapsed.TotalSeconds, DateTime.Now, f.NativeW, f.NativeH, ""));
                    }
                }
                catch (Exception e) { shots.Add(new Shot(null, clock.Elapsed.TotalSeconds, DateTime.Now, 0, 0, e.Message)); }
            }
        }
        finally { if (native != null) VideoSource.Release(native, 10000); }
        return shots;
    }

    /// The shape a frame is seen at: square pixels, as captured.
    static double Shape(Bitmap b) => (double)b.Width / b.Height;

    static Bitmap Lay(List<Shot> shots)
    {
        using var font = new Font("Consolas", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        int strip = font.Height + 6;
        int n = shots.Count;
        // Every cell the shape of the widest frame; the others fit inside it.
        double aspect = shots.Where(s => s.Picture != null).Select(s => Shape(s.Picture!)).DefaultIfEmpty(4.0 / 3).Max();

        // THE LAYOUT WITH THE LARGEST FRAMES that fits the largest image.
        int bestCols = 1, bestW = 0;
        for (int cols = 1; cols <= n; cols++)
        {
            int rows = (n + cols - 1) / cols;
            int byWidth = (Largest - Gap * (cols - 1)) / cols;
            int byHeight = (int)(((Largest - Gap * (rows - 1)) / (double)rows - strip) * aspect);
            int w = Math.Min(byWidth, byHeight);
            if (w > bestW) { bestW = w; bestCols = cols; }
        }
        int cw = Math.Max(16, bestW), ch = Math.Max(12, (int)Math.Round(cw / aspect));
        int c = bestCols, r = (n + c - 1) / c;
        var sheet = new Bitmap(c * cw + Gap * (c - 1), r * (ch + strip) + Gap * (r - 1), PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(24, 24, 28));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var stripBrush = new SolidBrush(Color.FromArgb(48, 48, 54));

        for (int i = 0; i < n; i++)
        {
            var s = shots[i];
            int x = (i % c) * (cw + Gap), y = (i / c) * (ch + strip + Gap);
            g.FillRectangle(Brushes.Black, x, y, cw, ch);
            if (s.Picture != null)
            {
                double shape = Shape(s.Picture);
                int w = cw, h = (int)Math.Round(cw / shape);
                if (h > ch) { h = ch; w = (int)Math.Round(ch * shape); }
                g.DrawImage(s.Picture, new Rectangle(x + (cw - w) / 2, y + (ch - h) / 2, w, h));
            }
            // THE TIME, BENEATH THE FRAME, in a strip of its own.
            var bar = new Rectangle(x, y + ch, cw, strip);
            g.FillRectangle(stripBrush, bar);
            string label = $"{i + 1}  +{s.At:0.00}s  {s.Clock:HH:mm:ss.fff}  " +
                           (s.Picture != null ? $"{s.Picture.Width}x{s.Picture.Height}" : s.Note);
            TextRenderer.DrawText(g, label, font, new Rectangle(bar.X + 4, bar.Y, bar.Width - 8, bar.Height),
                s.Picture != null ? Color.Gainsboro : Color.Orange,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        return sheet;
    }

    static byte[] Encode(Bitmap b, out string mime)
    {
        using var ms = new MemoryStream();
        b.Save(ms, ImageFormat.Png);
        if (ms.Length * 4 / 3 <= MostBytes) { mime = "image/png"; return ms.ToArray(); }
        var jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        foreach (long quality in new long[] { 92, 85, 75, 60 })
        {
            using var js = new MemoryStream();
            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            b.Save(js, jpeg, p);
            if (js.Length * 4 / 3 <= MostBytes || quality == 60) { mime = "image/jpeg"; return js.ToArray(); }
        }
        throw new InvalidOperationException("unreachable");
    }
}
