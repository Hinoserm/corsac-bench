// MCP over HTTP ("streamable HTTP", JSON replies only) on 127.0.0.1, for
// every assistant session at once. A session is named by the Mcp-Session-Id
// handed out at initialize; an id the bench does not know (the bench was
// restarted under it) simply starts a new client under that id, so a session
// never has to reconnect.
//
// A plain TcpListener and a hand-written HTTP/1.1 reader: HttpListener would
// need a URL reservation to answer to 127.0.0.1 as well as localhost.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace CorsacBench;

public static class Mcp
{
    public static int Port;
    public static string Error = "";

    static JsonObject Obj(params (string k, JsonNode? v)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv) o[k] = v;
        return o;
    }

    static JsonObject Schema(JsonObject props, params string[] required)
    {
        var s = Obj(("type", "object"), ("properties", props));
        if (required.Length > 0) s["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return s;
    }

    static JsonObject T(string type, string? description = null) =>
        description == null ? Obj(("type", type)) : Obj(("type", type), ("description", description));

    static JsonObject Props(params (string k, JsonObject v)[] extra)
    {
        var o = Obj(("port", T("string", "which COM port; the bench's default port unless given")));
        foreach (var (k, v) in extra) o[k] = v;
        return o;
    }

    static (string, JsonObject)[] Settings() => new[]
    {
        ("baud", T("integer")), ("bytesize", T("integer", "5-8")), ("parity", T("string", "N, E, O, M or S")),
        ("stopbits", T("number", "1, 1.5 or 2")), ("rtscts", T("boolean")), ("xonxoff", T("boolean")),
    };

    static JsonObject Tool(string name, string description, JsonObject schema) =>
        Obj(("name", name), ("description", description), ("inputSchema", schema));

    const string Shared = " The bench is one server shared by every assistant session and by the person at the bench's window: ports opened or closed here are opened or closed for all of them.";

    static readonly JsonArray Tools = new(
        Tool("serial_ports", "Every COM port on this machine, which are open on the bench with what settings, and which is the default." + Shared, Schema(new())),
        Tool("serial_open", "Open (connect to) a COM port with the given settings (defaults 115200 8N1, no flow control) and make it the default port. Reopening an open port applies the settings." + Shared,
            Schema(Props(Settings().Append(("default", T("boolean", "make it the default port (default true)"))).ToArray()))),
        Tool("serial_close", "Close (disconnect from) a COM port so another program can have it. What it received stays readable." + Shared, Schema(Props())),
        Tool("serial_configure", "Change a port's settings (baud, bytesize, parity, stopbits, rtscts, xonxoff), live if it is open.", Schema(Props(Settings()))),
        Tool("serial_default", "Make a port the one the serial tools use when none is named (for every client).", Schema(Props(), "port")),
        Tool("serial_status", "Whether a port is open, its settings, how much has arrived and how much of that this session has not read, any error, and who else is connected.", Schema(Props())),
        Tool("serial_read", "Everything the machine has sent on a port since this session last read (or waited), after waiting `seconds` for more. Every session has its own place in the stream.",
            Schema(Props(("seconds", T("number", "How long to collect first (default 1)"))))),
        Tool("serial_send", "Send text to the machine. A newline is appended unless newline is false. Use serial_command for a shell command whose output you want back.",
            Schema(Props(("text", T("string")), ("newline", T("boolean"))), "text")),
        Tool("serial_wait", "Wait until `text` appears in what the machine sends (up to `seconds`), returning everything up to and including it. Says if it timed out.",
            Schema(Props(("text", T("string")), ("seconds", T("number", "default 60"))), "text")),
        Tool("serial_command", "Run one shell command on the machine's serial console (it must be at a '# ' prompt) and return its output. Waits up to `seconds` for the next prompt. Commands from different sessions take turns.",
            Schema(Props(("command", T("string")), ("seconds", T("number", "default 120"))), "command")),
        Tool("serial_login", "Wait for a login: prompt (up to `seconds`) and log in as `user` (default root, no password). Returns the console output through the first shell prompt.",
            Schema(Props(("user", T("string")), ("seconds", T("number", "default 180"))))),
        Tool("serial_reset", "Reset the machine NOW by sending the kernel's magic sequence (ESC ESC ESC RESET) on the serial line, then wait up to `seconds` for the loader's banner. Works whenever the kernel is taking serial interrupts.",
            Schema(Props(("seconds", T("number", "how long to wait for 'CORSAC boot' afterwards, default 30"))))),
        Tool("serial_tail", "The last `chars` characters the machine sent on a port, regardless of what has been read already.",
            Schema(Props(("chars", T("integer", "default 4000"))))),
        Tool("serial_screen", "The port's terminal screen as the bench's terminal window shows it (full-screen programs such as nano draw here), optionally with `history` lines of scrollback above it.",
            Schema(Props(("history", T("integer", "scrollback lines to include, default 0"))))),
        Tool("serial_record_start", "Start recording one or more ports to a file, alongside anything else recording them. The file is a Windows path, a WSL path (/home/... is reached through \\\\wsl.localhost), or a bare name in the bench's recordings folder; left out, one is made from the ports and the time. Several ports in one file have each line marked with its port. Keeps running across bench restarts until stopped." + Shared,
            Schema(Obj(
                ("ports", T("string", "one port or several, comma-separated; the default port unless given")),
                ("path", T("string", "where to write it")),
                ("format", T("string", "raw (bytes as received), text (escape sequences and CRs removed), or timestamped (text, each line timed; the default)")),
                ("include_sent", T("boolean", "also record what is sent to the port, marked with who sent it (text formats; default true)")),
                ("append", T("boolean", "add to an existing file rather than replacing it (default true)"))))),
        Tool("serial_record_stop", "Stop recordings: by the id serial_record_start gave, by file path, by port (every recording of only that port), or all of them.",
            Schema(Obj(("id", T("string")), ("path", T("string")), ("port", T("string")), ("all", T("boolean"))))),
        Tool("serial_record_list", "Every recording running on the bench: id, ports, file, form, and how much it has written.", Schema(new())),
        Tool("bench_clients", "Every session connected to the bench, and what each is doing.", Schema(new())),
        Tool("vga_devices", "List the capture devices by index and name, marking the default.", Schema(new())),
        Tool("vga_select", "Make a capture device (by index or a part of its name) the default for vga_capture.", Schema(Obj(("device", T("string"))), "device")),
        Tool("vga_capture", "Grab one frame of the machine's VGA output as a PNG image (also saved as vga.png in the bench folder), at the resolution of the signal coming in. `device` is an index or a part of the device name; the default device unless given.",
            Schema(Obj(("device", T("string")))))
    );

    public static void Start(int port)
    {
        Port = port;
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        new Thread(() =>
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch (Exception e) { Error = e.Message; Thread.Sleep(1000); continue; }
                new Thread(() => Connection(c)) { IsBackground = true, Name = "mcp connection" }.Start();
            }
        }) { IsBackground = true, Name = "mcp listener" }.Start();
    }

    static string? ReadLine(Stream s)
    {
        var b = new List<byte>(128);
        while (true)
        {
            int c = s.ReadByte();
            if (c < 0) return b.Count == 0 ? null : Encoding.Latin1.GetString(b.ToArray());
            if (c == '\n') return Encoding.Latin1.GetString(b.ToArray()).TrimEnd('\r');
            b.Add((byte)c);
            if (b.Count > 65536) throw new IOException("header line too long");
        }
    }

    static void Connection(TcpClient tcp)
    {
        using var _ = tcp;
        tcp.NoDelay = true;
        var s = tcp.GetStream();
        try
        {
            while (true)
            {
                var request = ReadLine(s);
                if (request == null) return;
                if (request.Length == 0) continue;
                var parts = request.Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (string? h; !string.IsNullOrEmpty(h = ReadLine(s));)
                {
                    int colon = h.IndexOf(':');
                    if (colon > 0) headers[h[..colon].Trim()] = h[(colon + 1)..].Trim();
                }
                var body = Array.Empty<byte>();
                if (headers.TryGetValue("Content-Length", out var len) && int.TryParse(len, out int n) && n > 0)
                {
                    body = new byte[n];
                    s.ReadExactly(body);
                }
                bool keep = !(headers.TryGetValue("Connection", out var conn) && conn.Equals("close", StringComparison.OrdinalIgnoreCase));
                Handle(s, parts.Length > 1 ? parts[0] : "", parts.Length > 1 ? parts[1] : "", headers, body);
                if (!keep) return;
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    static void Respond(Stream s, int status, string reason, byte[]? body = null, string type = "application/json", string? session = null)
    {
        var h = new StringBuilder();
        h.Append($"HTTP/1.1 {status} {reason}\r\n");
        if (body != null) h.Append($"Content-Type: {type}\r\n");
        h.Append($"Content-Length: {body?.Length ?? 0}\r\n");
        if (session != null) h.Append($"Mcp-Session-Id: {session}\r\n");
        h.Append("\r\n");
        s.Write(Encoding.ASCII.GetBytes(h.ToString()));
        if (body != null) s.Write(body);
        s.Flush();
    }

    static void Handle(Stream s, string method, string path, Dictionary<string, string> headers, byte[] body)
    {
        headers.TryGetValue("Mcp-Session-Id", out var session);
        if (path.StartsWith("/status"))
        {
            Respond(s, 200, "OK", Encoding.UTF8.GetBytes(StatusText()), "text/plain; charset=utf-8");
            return;
        }
        if (!path.StartsWith("/mcp"))
        {
            Respond(s, 404, "Not Found");
            return;
        }
        if (method == "DELETE")
        {
            if (session != null) Bench.Forget(session);
            Respond(s, 200, "OK");
            return;
        }
        if (method != "POST")
        {
            Respond(s, 405, "Method Not Allowed");
            return;
        }
        JsonNode? msg;
        try { msg = JsonNode.Parse(body); }
        catch
        {
            Respond(s, 400, "Bad Request", Encoding.UTF8.GetBytes(Error400("parse error")));
            return;
        }
        // A batch is answered as a batch; notifications alone get 202.
        if (msg is JsonArray batch)
        {
            var replies = new JsonArray();
            foreach (var m in batch)
                if (m is JsonObject o && One(o, ref session) is JsonObject r) replies.Add(r);
            if (replies.Count == 0) Respond(s, 202, "Accepted", session: session);
            else Respond(s, 200, "OK", Encoding.UTF8.GetBytes(replies.ToJsonString()), session: session);
            return;
        }
        if (msg is not JsonObject one)
        {
            Respond(s, 400, "Bad Request", Encoding.UTF8.GetBytes(Error400("not a JSON-RPC message")));
            return;
        }
        var reply = One(one, ref session);
        if (reply == null) Respond(s, 202, "Accepted", session: session);
        else Respond(s, 200, "OK", Encoding.UTF8.GetBytes(reply.ToJsonString()), session: session);
    }

    static string Error400(string what) =>
        Obj(("jsonrpc", "2.0"), ("id", null), ("error", Obj(("code", -32700), ("message", what)))).ToJsonString();

    static JsonObject? One(JsonObject msg, ref string? session)
    {
        var id = msg["id"]?.DeepClone();
        var method = msg["method"]?.ToString() ?? "";
        var p = msg["params"] as JsonObject ?? new JsonObject();
        JsonObject Result(JsonNode r) => Obj(("jsonrpc", "2.0"), ("id", id), ("result", r));

        if (method == "initialize")
        {
            var info = p["clientInfo"] as JsonObject;
            session ??= Guid.NewGuid().ToString("N");
            Bench.Client(session, info?["name"]?.ToString() ?? "client");
            return Result(Obj(
                ("protocolVersion", p["protocolVersion"]?.ToString() ?? "2025-03-26"),
                ("capabilities", Obj(("tools", new JsonObject()))),
                ("serverInfo", Obj(("name", "corsac-bench"), ("version", "2.0"))),
                ("instructions", "The CORSAC bench: the real machine's serial consoles and VGA capture, shared by every session and by the person at the bench's window.")));
        }
        var client = Bench.Client(session ??= Guid.NewGuid().ToString("N"));
        if (id == null) return null;           // a notification
        switch (method)
        {
            case "ping":
                return Result(new JsonObject());
            case "tools/list":
                return Result(Obj(("tools", Tools.DeepClone())));
            case "tools/call":
                JsonObject result;
                try { result = Bench.Call(client, p["name"]?.ToString() ?? "", p["arguments"] as JsonObject ?? new JsonObject()); }
                catch (Exception e)
                {
                    result = Obj(("content", new JsonArray(Obj(("type", "text"), ("text", "error: " + e.Message)))), ("isError", true));
                }
                return Result(result);
        }
        return Obj(("jsonrpc", "2.0"), ("id", id), ("error", Obj(("code", -32601), ("message", "unknown method " + method))));
    }

    public static string StatusText()
    {
        var sb = new StringBuilder();
        sb.Append($"corsac-bench on 127.0.0.1:{Port}, default port {Bench.DefaultPort}, default capture {Bench.DefaultVga}\n");
        lock (Bench.Lines)
            foreach (var l in Bench.Lines.Values)
                sb.Append($"  {l.Describe()} received={l.End}{(l.Error != "" ? " error=" + l.Error : "")}\n");
        lock (Bench.Clients)
            foreach (var c in Bench.Clients.Values)
                sb.Append($"  client {c.Label} seen {(DateTime.UtcNow - c.Seen).TotalSeconds:0}s ago{(c.Doing != "" ? " doing " + c.Doing : "")}\n");
        return sb.ToString();
    }
}
