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

/// <summary>One vgagrab.py process. Each device gets its own, so a device that is slow or has no signal never holds up another.</summary>
sealed class VgaHelper
{
    Process? _p;
    Stream? _in, _out;
    readonly object _gate = new();
    string _error = "";

    static string HelperPath => Path.Combine(AppContext.BaseDirectory, "vgagrab.py");

    void Start()
    {
        if (_p is { HasExited: false }) return;
        var psi = new ProcessStartInfo(Bench.Config.Python)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi.ArgumentList.Add(HelperPath);
        psi.ArgumentList.Add("serve");
        _p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + Bench.Config.Python);
        _p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _error = e.Data; };
        _p.BeginErrorReadLine();
        _in = _p.StandardInput.BaseStream;
        _out = _p.StandardOutput.BaseStream;
    }

    public void Stop()
    {
        lock (_gate)
        {
            // Its stdin closing is its signal to go.
            try { _in?.Close(); } catch { }
            try { if (_p is { HasExited: false } && !_p.WaitForExit(2000)) _p.Kill(); } catch { }
            _p = null;
        }
    }

    string ReadLine(Stream s)
    {
        var b = new List<byte>(256);
        while (true)
        {
            int c = s.ReadByte();
            if (c < 0) throw new IOException("the capture helper went away" + (_error != "" ? ": " + _error : ""));
            if (c == '\n') return Encoding.UTF8.GetString(b.ToArray());
            b.Add((byte)c);
        }
    }

    /// <summary>One request, its reply and any raw bytes after it. Restarts a helper that died.</summary>
    public (JsonObject reply, byte[] data) Ask(JsonObject req)
    {
        lock (_gate)
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
                    try { _p?.Kill(); } catch { }
                    _p = null;
                }
            }
        }
    }
}

public static class Vga
{
    static readonly Dictionary<string, VgaHelper> Helpers = new();

    /// <summary>The helper for a device; "" is the one that only lists devices.</summary>
    static VgaHelper For(string device)
    {
        lock (Helpers)
        {
            if (!Helpers.TryGetValue(device, out var h)) Helpers[device] = h = new VgaHelper();
            return h;
        }
    }

    static (JsonObject reply, byte[] data) Ask(JsonObject req) => For(req["device"]?.ToString() ?? "").Ask(req);

    public static void Stop()
    {
        List<VgaHelper> all;
        lock (Helpers) { all = Helpers.Values.ToList(); Helpers.Clear(); }
        foreach (var h in all) h.Stop();
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
