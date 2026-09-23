// The capture devices, through vgagrab.py: DirectShow is Python's business
// here. One helper process serves every viewer and every client; it keeps a
// device open while frames are being asked for and lets it go after that.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json.Nodes;

namespace CorsacBench;

public sealed class VgaFrame : IDisposable
{
    public required string Device;
    public required long Seq;
    public required Bitmap Picture;
    public int NativeW, NativeH;
    public double Fps;
    public bool Follow;

    public void Dispose() => Picture.Dispose();
}

public static class Vga
{
    static Process? _helper;
    static Stream? _in, _out;
    static readonly object Gate = new();
    public static string Error = "";

    static string HelperPath => Path.Combine(AppContext.BaseDirectory, "vgagrab.py");

    static void Start()
    {
        if (_helper is { HasExited: false }) return;
        var psi = new ProcessStartInfo(Bench.Config.Python)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi.ArgumentList.Add(HelperPath);
        psi.ArgumentList.Add("serve");
        _helper = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + Bench.Config.Python);
        _helper.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Error = e.Data; };
        _helper.BeginErrorReadLine();
        _in = _helper.StandardInput.BaseStream;
        _out = _helper.StandardOutput.BaseStream;
    }

    public static void Stop()
    {
        lock (Gate)
        {
            try { _in?.Close(); } catch { }
            try { if (_helper is { HasExited: false } && !_helper.WaitForExit(2000)) _helper.Kill(); } catch { }
            _helper = null;
        }
    }

    static string ReadLine(Stream s)
    {
        var b = new List<byte>(256);
        while (true)
        {
            int c = s.ReadByte();
            if (c < 0) throw new IOException("the capture helper went away" + (Error != "" ? ": " + Error : ""));
            if (c == '\n') return Encoding.UTF8.GetString(b.ToArray());
            b.Add((byte)c);
        }
    }

    /// <summary>One request, its reply and any raw bytes after it. Restarts a helper that died.</summary>
    static (JsonObject reply, byte[] data) Ask(JsonObject req)
    {
        lock (Gate)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Start();
                    _in!.Write(Encoding.UTF8.GetBytes(req.ToJsonString() + "\n"));
                    _in.Flush();
                    var reply = JsonNode.Parse(ReadLine(_out!))!.AsObject();
                    var data = Array.Empty<byte>();
                    if (reply["len"] is JsonNode n)
                    {
                        data = new byte[(int)n];
                        _out!.ReadExactly(data);
                    }
                    if (reply["error"] is JsonNode e) throw new InvalidOperationException(e.ToString());
                    return (reply, data);
                }
                catch (IOException) when (attempt == 0)
                {
                    try { _helper?.Kill(); } catch { }
                    _helper = null;
                }
            }
        }
    }

    public static List<string> Devices() =>
        Ask(new JsonObject { ["op"] = "devices" }).reply["devices"]!.AsArray().Select(n => n!.ToString()).ToList();

    public static (int w, int h)? Native(string device, out List<(int w, int h)> sizes)
    {
        var r = Ask(new JsonObject { ["op"] = "formats", ["device"] = device }).reply;
        sizes = r["sizes"]!.AsArray().Select(s => ((int)s![0]!, (int)s[1]!)).ToList();
        return r["native"] is JsonArray a ? ((int)a[0]!, (int)a[1]!) : null;
    }

    /// <summary>Capture at the signal's size (w = 0) or a fixed one.</summary>
    public static void Open(string device, int w, int h) =>
        Ask(new JsonObject { ["op"] = "open", ["device"] = device, ["follow"] = w == 0, ["width"] = w, ["height"] = h });

    public static void Close(string device) => Ask(new JsonObject { ["op"] = "close", ["device"] = device });

    /// <summary>The next frame after `since`; waits up to `wait` seconds for one.</summary>
    public static VgaFrame Frame(string device, long since, double wait = 5)
    {
        var (r, data) = Ask(new JsonObject { ["op"] = "frame", ["device"] = device, ["since"] = since, ["wait"] = wait });
        int w = (int)r["width"]!, h = (int)r["height"]!;
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(data, y * w * 3, bits.Scan0 + y * bits.Stride, w * 3);
        }
        finally { bmp.UnlockBits(bits); }
        var nat = r["native"] as JsonArray;
        return new VgaFrame
        {
            Device = r["device"]!.ToString(), Seq = (long)r["seq"]!, Picture = bmp,
            NativeW = nat != null ? (int)nat[0]! : w, NativeH = nat != null ? (int)nat[1]! : h,
            Fps = (double)r["fps"]!, Follow = (bool)r["follow"]!,
        };
    }

    /// <summary>A device named by a part of its name or its index in the list.</summary>
    public static string Resolve(string? want)
    {
        want ??= Bench.DefaultVga;
        var all = Devices();
        if (int.TryParse(want, out int i))
            return i >= 0 && i < all.Count ? all[i] : throw new ArgumentException("no capture device with index " + want);
        return all.FirstOrDefault(d => d.Equals(want, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(d => d.Contains(want, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"no capture device matches \"{want}\"; have {string.Join(", ", all)}");
    }
}
