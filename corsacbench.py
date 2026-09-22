#!/usr/bin/env python3
"""The bench: the real machine's serial console and its VGA capture, as MCP tools.

Runs on the Windows side, where COM5 and the DirectShow capture devices are,
and speaks MCP over stdio, so a code assistant in WSL (which can start a
Windows Python directly) and the desktop app can both drive the board. Python 3.9,
pyserial, opencv-python and pygrabber; nothing else. The MCP wire format is
small enough to speak by hand, which keeps the SDK's 3.10 requirement out of
a 3.9 install.

  python corsacbench.py                      # MCP server on stdin/stdout
  python corsacbench.py --self-test          # lists devices, grabs a frame

Environment: CORSAC_COM (COM5), CORSAC_BAUD (115200), CORSAC_VGA (a substring
of the capture device's name, "00-1 Pro Capture Dual DVI"), CORSAC_DIR (where
the serial log and frames are kept, C:\\CORSAC\\bench).

Any number of COM ports can be open at once; each is read continuously by a
thread from the moment it is opened and appended to serial-<PORT>.log, and
the tools hand out what arrived since the last look, so nothing the machine
says between two calls is lost. The default port (CORSAC_COM) is opened at
start; serial_open/serial_close connect and disconnect others, and
serial_configure changes speed and framing live.
"""
import base64, json, os, re, sys, threading, time

COM = os.environ.get("CORSAC_COM", "COM5")
BAUD = int(os.environ.get("CORSAC_BAUD", "115200"))
VGA = os.environ.get("CORSAC_VGA", "00-1 Pro Capture Dual DVI")
DIR = os.environ.get("CORSAC_DIR", r"C:\CORSAC\bench")
MAGIC = b"\x1b\x1b\x1bRESET"           # os/kernel/drivers/uart8250.cor, MagicByte
PROMPT = b"\n# "

# ---- the serial line -----------------------------------------------------

class Line:
    """One serial port, read by a thread into a growing buffer and a log."""
    def __init__(self, name):
        self.name = name
        self.port = None
        self.buf = bytearray()
        self.cursor = 0                 # what the tools have already handed out
        self.lock = threading.Condition()
        self.error = ""
        self.log = None
        self.settings = {"baud": BAUD, "bytesize": 8, "parity": "N", "stopbits": 1, "rtscts": False, "xonxoff": False}

    def describe(self):
        st = self.settings
        return "%s %d %d%s%s%s%s%s" % (self.name, st["baud"], st["bytesize"], st["parity"], st["stopbits"],
            " rtscts" if st["rtscts"] else "", " xonxoff" if st["xonxoff"] else "",
            " open" if self.is_open() else " closed")

    def is_open(self):
        return bool(self.port and self.port.is_open)

    def open(self, **settings):
        import serial
        self.settings.update({k: v for k, v in settings.items() if v is not None})
        st = self.settings
        os.makedirs(DIR, exist_ok=True)
        if self.log is None:
            self.log = open(os.path.join(DIR, "serial-%s.log" % self.name), "ab", buffering=0)
        self.log.write(("\n---- bench opened %s %s ----\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), self.describe())).encode())
        self.port = serial.Serial(self.name, st["baud"], bytesize=st["bytesize"], parity=st["parity"],
                                  stopbits=st["stopbits"], rtscts=st["rtscts"], xonxoff=st["xonxoff"], timeout=0.05)
        self.error = ""
        threading.Thread(target=self.pump, args=(self.port,), daemon=True).start()

    def close(self):
        port, self.port = self.port, None
        if port is not None and port.is_open:
            port.close()
            self.log.write(("\n---- bench closed %s %s ----\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), self.name)).encode())

    def configure(self, **settings):
        """Change the line's settings; applied live if the port is open."""
        self.settings.update({k: v for k, v in settings.items() if v is not None})
        st = self.settings
        if self.is_open():
            p = self.port
            p.baudrate = st["baud"]; p.bytesize = st["bytesize"]; p.parity = st["parity"]
            p.stopbits = st["stopbits"]; p.rtscts = st["rtscts"]; p.xonxoff = st["xonxoff"]
            self.log.write(("\n---- bench reconfigured %s ----\n" % self.describe()).encode())

    def pump(self, port):
        while self.port is port and port.is_open:
            try:
                data = port.read(4096)
            except Exception as e:
                if self.port is port:
                    self.error = str(e)
                    time.sleep(1)
                continue
            if data:
                with self.lock:
                    self.buf += data
                    self.lock.notify_all()
                self.log.write(data)

    def send(self, data: bytes):
        if not self.is_open():
            raise RuntimeError("%s is not open" % self.name)
        self.port.write(data)
        self.port.flush()
        self.log.write(b"\n<<< " + data + b"\n")

    def take(self) -> bytes:
        """Everything received since the last take."""
        with self.lock:
            out = bytes(self.buf[self.cursor:])
            self.cursor = len(self.buf)
        return out

    def wait(self, needle: bytes, seconds: float):
        """Blocks until `needle` shows up after the cursor, or the time is up.
        Returns (found, text up to and including the needle or all of it)."""
        end = time.time() + seconds
        with self.lock:
            while True:
                at = self.buf.find(needle, self.cursor)
                if at >= 0:
                    stop = at + len(needle)
                    out = bytes(self.buf[self.cursor:stop]); self.cursor = stop
                    return True, out
                left = end - time.time()
                if left <= 0:
                    out = bytes(self.buf[self.cursor:]); self.cursor = len(self.buf)
                    return False, out
                self.lock.wait(min(left, 0.5))

    def tail(self, chars: int) -> bytes:
        with self.lock:
            return bytes(self.buf[-chars:])

LINES = {}                  # name -> Line, for every port opened this session
DEFAULT = {"port": COM, "vga": VGA}

def line(name=None) -> Line:
    name = (name or DEFAULT["port"]).upper()
    if name not in LINES:
        LINES[name] = Line(name)
    return LINES[name]

def com_ports():
    import serial.tools.list_ports as l
    return [(p.device, p.description) for p in sorted(l.comports(), key=lambda p: p.device)]

def settings_of(a):
    """Serial settings from tool arguments; None where not given."""
    def parity(v):
        if v is None: return None
        v = str(v).upper()[:1]
        if v not in "NEOMS": raise ValueError("parity must be N, E, O, M or S")
        return v
    return dict(baud=a.get("baud"), bytesize=a.get("bytesize"), parity=parity(a.get("parity")),
                stopbits=a.get("stopbits"), rtscts=a.get("rtscts"), xonxoff=a.get("xonxoff"))

def text(b: bytes) -> str:
    return b.decode("latin-1").replace("\r", "")

# ---- the picture -----------------------------------------------------------

def vga_devices():
    from pygrabber.dshow_graph import FilterGraph
    return list(enumerate(FilterGraph().get_input_devices()))

def vga_index(name):
    devices = vga_devices()
    for index, device in devices:
        if name.lower() in device.lower():
            return index, device
    raise ValueError("no capture device matches %r; have %s" % (name, [d for _, d in devices]))

def vga_grab(name=None, warm=6):
    """One frame as PNG bytes. DirectShow devices deliver a few dark or torn
    frames after opening, so `warm` are thrown away first."""
    import cv2
    index, device = vga_index(name or DEFAULT["vga"])
    cap = cv2.VideoCapture(index, cv2.CAP_DSHOW)
    try:
        if not cap.isOpened():
            raise RuntimeError("could not open capture device %d (%s)" % (index, device))
        # The device's default is 640x480, which makes 80-column text a
        # smear; ask for the board's native size and take what it gives.
        cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1024)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 768)
        frame = None
        for _ in range(warm + 1):
            ok, got = cap.read()
            if ok:
                frame = got
        if frame is None:
            raise RuntimeError("capture device %d (%s) delivered no frame" % (index, device))
    finally:
        cap.release()
    ok, png = cv2.imencode(".png", frame)
    if not ok:
        raise RuntimeError("PNG encoding failed")
    os.makedirs(DIR, exist_ok=True)
    path = os.path.join(DIR, "vga.png")
    with open(path, "wb") as f:
        f.write(png.tobytes())
    return png.tobytes(), device, frame.shape[1], frame.shape[0], path

# ---- the tools -------------------------------------------------------------

PORT_ARG = {"port": {"type": "string", "description": "which COM port; the default port unless given"}}
SETTINGS = {"baud": {"type": "integer"}, "bytesize": {"type": "integer", "description": "5-8"},
            "parity": {"type": "string", "description": "N, E, O, M or S"}, "stopbits": {"type": "number", "description": "1, 1.5 or 2"},
            "rtscts": {"type": "boolean"}, "xonxoff": {"type": "boolean"}}
def props(*extra):
    out = dict(PORT_ARG)
    for e in extra: out.update(e)
    return out

TOOLS = [
    {"name": "serial_ports", "description": "Every COM port on this machine, which are open here with what settings, and which is the default.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "serial_open", "description": "Open (connect to) a COM port with the given settings (defaults 115200 8N1, no flow control) and make it the default port. Reopening an open port applies the settings.",
     "inputSchema": {"type": "object", "properties": props(SETTINGS, {"default": {"type": "boolean", "description": "make it the default port (default true)"}})}},
    {"name": "serial_close", "description": "Close (disconnect from) a COM port so another program can have it. What it received stays readable.",
     "inputSchema": {"type": "object", "properties": props()}},
    {"name": "serial_configure", "description": "Change a port's settings (baud, bytesize, parity, stopbits, rtscts, xonxoff), live if it is open.",
     "inputSchema": {"type": "object", "properties": props(SETTINGS)}},
    {"name": "serial_default", "description": "Make a port the one the other serial tools use when none is named.",
     "inputSchema": {"type": "object", "properties": props(), "required": ["port"]}},
    {"name": "serial_status", "description": "Whether a port is open, its settings, how much has arrived, and any error.",
     "inputSchema": {"type": "object", "properties": props()}},
    {"name": "serial_read", "description": "Everything the machine has sent on a port since the last read (or wait), after waiting `seconds` for more. Empty when nothing arrived.",
     "inputSchema": {"type": "object", "properties": props({"seconds": {"type": "number", "description": "How long to collect first (default 1)"}})}},
    {"name": "serial_send", "description": "Send text to the machine. A newline is appended unless newline is false. Use serial_command for a shell command whose output you want back.",
     "inputSchema": {"type": "object", "properties": props({"text": {"type": "string"}, "newline": {"type": "boolean"}}), "required": ["text"]}},
    {"name": "serial_wait", "description": "Wait until `text` appears in what the machine sends (up to `seconds`), returning everything up to and including it. Says if it timed out.",
     "inputSchema": {"type": "object", "properties": props({"text": {"type": "string"}, "seconds": {"type": "number", "description": "default 60"}}), "required": ["text"]}},
    {"name": "serial_command", "description": "Run one shell command on the machine's serial console (it must be at a '# ' prompt) and return its output. Waits up to `seconds` for the next prompt.",
     "inputSchema": {"type": "object", "properties": props({"command": {"type": "string"}, "seconds": {"type": "number", "description": "default 120"}}), "required": ["command"]}},
    {"name": "serial_login", "description": "Wait for a login: prompt (up to `seconds`) and log in as `user` (default root, no password). Returns the console output through the first shell prompt.",
     "inputSchema": {"type": "object", "properties": props({"user": {"type": "string"}, "seconds": {"type": "number", "description": "default 180"}})}},
    {"name": "serial_reset", "description": "Reset the machine NOW by sending the kernel's magic sequence (ESC ESC ESC RESET) on the serial line, then wait up to `seconds` for the loader's banner. Works whenever the kernel is taking serial interrupts.",
     "inputSchema": {"type": "object", "properties": props({"seconds": {"type": "number", "description": "how long to wait for 'CORSAC boot' afterwards, default 30"}})}},
    {"name": "serial_tail", "description": "The last `chars` characters the machine sent on a port, regardless of what has been read already.",
     "inputSchema": {"type": "object", "properties": props({"chars": {"type": "integer", "description": "default 4000"}})}},
    {"name": "vga_devices", "description": "List the capture devices by DirectShow index and name, marking the default.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "vga_select", "description": "Make a capture device (by index or a substring of its name) the default for vga_capture.",
     "inputSchema": {"type": "object", "properties": {"device": {"type": "string"}}, "required": ["device"]}},
    {"name": "vga_capture", "description": "Grab one frame of the machine's VGA output as a PNG image (also saved as vga.png in the bench folder). `device` is an index or a substring of the device name; the default device unless given.",
     "inputSchema": {"type": "object", "properties": {"device": {"type": "string"}}}},
]

def ok(s): return {"content": [{"type": "text", "text": s}]}

def device_name(v):
    """A device argument is an index or a name substring; either way a name."""
    if v is None: return None
    v = str(v)
    if v.isdigit():
        for i, d in vga_devices():
            if i == int(v): return d
        raise ValueError("no capture device with index " + v)
    return v

def call(name, a):
    if name == "serial_ports":
        rows = []
        for device, description in com_ports():
            l = LINES.get(device.upper())
            rows.append("%s | %s | %s%s" % (device, description, l.describe() if l else "not opened here",
                                             " | DEFAULT" if device.upper() == DEFAULT["port"].upper() else ""))
        return ok("\n".join(rows) or "no COM ports")
    if name == "serial_open":
        l = line(a.get("port"))
        if l.is_open(): l.close()
        l.open(**settings_of(a))
        if a.get("default", True): DEFAULT["port"] = l.name
        return ok("opened " + l.describe() + (" (default)" if DEFAULT["port"] == l.name else ""))
    if name == "serial_close":
        l = line(a.get("port")); l.close()
        return ok("closed " + l.name)
    if name == "serial_configure":
        l = line(a.get("port")); l.configure(**settings_of(a))
        return ok("now " + l.describe())
    if name == "serial_default":
        DEFAULT["port"] = a["port"].upper()
        return ok("default port is " + DEFAULT["port"])
    if name == "serial_status":
        l = line(a.get("port"))
        with l.lock:
            have, seen = len(l.buf), l.cursor
        return ok("%s received=%d unread=%d error=%r log=%s%s" % (
            l.describe(), have, have - seen, l.error, os.path.join(DIR, "serial-%s.log" % l.name),
            " (default)" if DEFAULT["port"] == l.name else ""))
    if name == "serial_read":
        l = line(a.get("port"))
        time.sleep(float(a.get("seconds", 1)))
        return ok(text(l.take()))
    if name == "serial_send":
        l = line(a.get("port"))
        data = a["text"].encode("latin-1") + (b"\n" if a.get("newline", True) else b"")
        l.send(data)
        return ok("sent %d bytes on %s" % (len(data), l.name))
    if name == "serial_wait":
        l = line(a.get("port"))
        found, out = l.wait(a["text"].encode("latin-1"), float(a.get("seconds", 60)))
        return ok(text(out) + ("" if found else "\n[timed out waiting for %r]" % a["text"]))
    if name == "serial_command":
        l = line(a.get("port"))
        l.take()
        l.send(a["command"].encode("latin-1") + b"\n")
        found, out = l.wait(PROMPT, float(a.get("seconds", 120)))
        return ok(text(out) + ("" if found else "\n[no prompt came back within the time]"))
    if name == "serial_login":
        l = line(a.get("port"))
        found, out = l.wait(b"login:", float(a.get("seconds", 180)))
        if not found:
            return ok(text(out) + "\n[no login prompt within the time]")
        time.sleep(0.5)
        l.send(a.get("user", "root").encode("latin-1") + b"\n")
        found2, out2 = l.wait(b"# ", 90)
        return ok(text(out + out2) + ("" if found2 else "\n[no shell prompt after logging in]"))
    if name == "serial_reset":
        l = line(a.get("port"))
        l.take()
        l.send(MAGIC)
        found, out = l.wait(b"CORSAC boot", float(a.get("seconds", 30)))
        return ok(("machine reset; loader is up\n" if found else "no loader banner seen after the reset sequence\n") + text(out))
    if name == "serial_tail":
        l = line(a.get("port"))
        return ok(text(l.tail(int(a.get("chars", 4000)))))
    if name == "vga_devices":
        return ok("\n".join("%d: %s%s" % (i, d, "  (default)" if DEFAULT["vga"].lower() in d.lower() else "") for i, d in vga_devices()))
    if name == "vga_select":
        index, device = vga_index(device_name(a["device"]))
        DEFAULT["vga"] = device
        return ok("default capture device is %d: %s" % (index, device))
    if name == "vga_capture":
        png, device, w, h, path = vga_grab(device_name(a.get("device")))
        return {"content": [
            {"type": "text", "text": "%s, %dx%d, saved to %s" % (device, w, h, path)},
            {"type": "image", "data": base64.b64encode(png).decode("ascii"), "mimeType": "image/png"}]}
    raise ValueError("unknown tool " + name)

# ---- MCP over stdio -----------------------------------------------------

def serve():
    out = sys.stdout.buffer
    def reply(msg):
        out.write((json.dumps(msg) + "\n").encode("utf-8")); out.flush()
    try:
        line().open()
    except Exception as e:
        line().error = "serial did not open: %s" % e
    for raw in sys.stdin.buffer:
        raw = raw.strip()
        if not raw:
            continue
        try:
            msg = json.loads(raw)
        except ValueError:
            continue
        ident, method, params = msg.get("id"), msg.get("method", ""), msg.get("params") or {}
        if method == "initialize":
            reply({"jsonrpc": "2.0", "id": ident, "result": {
                "protocolVersion": params.get("protocolVersion", "2024-11-05"),
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "corsac-bench", "version": "1.0"}}})
        elif method == "tools/list":
            reply({"jsonrpc": "2.0", "id": ident, "result": {"tools": TOOLS}})
        elif method == "tools/call":
            try:
                result = call(params.get("name", ""), params.get("arguments") or {})
            except Exception as e:
                result = {"content": [{"type": "text", "text": "error: %s" % e}], "isError": True}
            reply({"jsonrpc": "2.0", "id": ident, "result": result})
        elif method == "ping":
            reply({"jsonrpc": "2.0", "id": ident, "result": {}})
        elif ident is not None:
            reply({"jsonrpc": "2.0", "id": ident, "error": {"code": -32601, "message": "unknown method " + method}})

def self_test():
    print("capture devices:")
    for i, d in vga_devices():
        print(" ", i, d)
    png, device, w, h, path = vga_grab()
    print("frame from %s: %dx%d, %d bytes PNG -> %s" % (device, w, h, len(png), path))
    print("COM ports:")
    for d, desc in com_ports():
        print(" ", d, "|", desc)
    l = line(); l.open()
    print("serial %s open; collecting two seconds..." % l.describe())
    time.sleep(2)
    print(text(l.take())[-500:] or "(nothing arrived)")

if __name__ == "__main__":
    if "--self-test" in sys.argv:
        self_test()
    else:
        serve()
