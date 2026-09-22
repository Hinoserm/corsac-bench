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

Serial output is read continuously by a thread from the moment the server
starts and appended to serial.log; the tools hand out what arrived since the
last look, so nothing the machine says between two calls is lost.
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
    def __init__(self):
        self.port = None
        self.buf = bytearray()
        self.cursor = 0                 # what the tools have already handed out
        self.lock = threading.Condition()
        self.error = ""
        self.log = None

    def open(self):
        import serial
        os.makedirs(DIR, exist_ok=True)
        self.log = open(os.path.join(DIR, "serial.log"), "ab", buffering=0)
        self.log.write(("\n---- bench opened %s %s at %d ----\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), COM, BAUD)).encode())
        self.port = serial.Serial(COM, BAUD, timeout=0.05)
        threading.Thread(target=self.pump, daemon=True).start()

    def pump(self):
        while True:
            try:
                data = self.port.read(4096)
            except Exception as e:
                self.error = str(e)
                time.sleep(1)
                continue
            if data:
                with self.lock:
                    self.buf += data
                    self.lock.notify_all()
                self.log.write(data)

    def send(self, data: bytes):
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

LINE = Line()

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
    index, device = vga_index(name or VGA)
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

TOOLS = [
    {"name": "serial_status", "description": "Whether the serial line is open, how much has arrived, and any error.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "serial_read", "description": "Everything the machine has sent since the last read (or wait), after waiting `seconds` for more. Empty when nothing arrived.",
     "inputSchema": {"type": "object", "properties": {"seconds": {"type": "number", "description": "How long to collect first (default 1)"}}}},
    {"name": "serial_send", "description": "Send text to the machine. A newline is appended unless newline is false. Use serial_command for a shell command whose output you want back.",
     "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}, "newline": {"type": "boolean"}}, "required": ["text"]}},
    {"name": "serial_wait", "description": "Wait until `text` appears in what the machine sends (up to `seconds`), returning everything up to and including it. Says if it timed out.",
     "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}, "seconds": {"type": "number", "description": "default 60"}}, "required": ["text"]}},
    {"name": "serial_command", "description": "Run one shell command on the machine's serial console (it must be at a '# ' prompt) and return its output. Waits up to `seconds` for the next prompt.",
     "inputSchema": {"type": "object", "properties": {"command": {"type": "string"}, "seconds": {"type": "number", "description": "default 120"}}, "required": ["command"]}},
    {"name": "serial_login", "description": "Wait for a login: prompt (up to `seconds`) and log in as `user` (default root, no password). Returns the console output through the first shell prompt.",
     "inputSchema": {"type": "object", "properties": {"user": {"type": "string"}, "seconds": {"type": "number", "description": "default 180"}}}},
    {"name": "serial_reset", "description": "Reset the machine NOW by sending the kernel's magic sequence (ESC ESC ESC RESET) on the serial line, then wait up to `seconds` for the loader's banner. Works whenever the kernel is taking serial interrupts.",
     "inputSchema": {"type": "object", "properties": {"seconds": {"type": "number", "description": "how long to wait for 'CORSAC boot' afterwards, default 30"}}}},
    {"name": "serial_tail", "description": "The last `chars` characters the machine sent, regardless of what has been read already (the log so far).",
     "inputSchema": {"type": "object", "properties": {"chars": {"type": "integer", "description": "default 4000"}}}},
    {"name": "vga_devices", "description": "List the capture devices by DirectShow index and name.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "vga_capture", "description": "Grab one frame of the machine's VGA output as a PNG image (also saved as vga.png in the bench folder). `device` is a substring of the device name; the default is the one the board is on.",
     "inputSchema": {"type": "object", "properties": {"device": {"type": "string"}}}},
]

def ok(s): return {"content": [{"type": "text", "text": s}]}

def call(name, a):
    if name == "serial_status":
        with LINE.lock:
            have, seen = len(LINE.buf), LINE.cursor
        return ok("port=%s baud=%d open=%s received=%d unread=%d error=%r log=%s" % (
            COM, BAUD, bool(LINE.port and LINE.port.is_open), have, have - seen, LINE.error, os.path.join(DIR, "serial.log")))
    if name == "serial_read":
        time.sleep(float(a.get("seconds", 1)))
        return ok(text(LINE.take()))
    if name == "serial_send":
        data = a["text"].encode("latin-1") + (b"\n" if a.get("newline", True) else b"")
        LINE.send(data)
        return ok("sent %d bytes" % len(data))
    if name == "serial_wait":
        found, out = LINE.wait(a["text"].encode("latin-1"), float(a.get("seconds", 60)))
        return ok(text(out) + ("" if found else "\n[timed out waiting for %r]" % a["text"]))
    if name == "serial_command":
        LINE.take()
        LINE.send(a["command"].encode("latin-1") + b"\n")
        found, out = LINE.wait(PROMPT, float(a.get("seconds", 120)))
        return ok(text(out) + ("" if found else "\n[no prompt came back within the time]"))
    if name == "serial_login":
        found, out = LINE.wait(b"login:", float(a.get("seconds", 180)))
        if not found:
            return ok(text(out) + "\n[no login prompt within the time]")
        time.sleep(0.5)
        LINE.send(a.get("user", "root").encode("latin-1") + b"\n")
        found2, out2 = LINE.wait(b"# ", 90)
        return ok(text(out + out2) + ("" if found2 else "\n[no shell prompt after logging in]"))
    if name == "serial_reset":
        LINE.take()
        LINE.send(MAGIC)
        found, out = LINE.wait(b"CORSAC boot", float(a.get("seconds", 30)))
        return ok(("machine reset; loader is up\n" if found else "no loader banner seen after the reset sequence\n") + text(out))
    if name == "serial_tail":
        return ok(text(LINE.tail(int(a.get("chars", 4000)))))
    if name == "vga_devices":
        return ok("\n".join("%d: %s" % (i, d) for i, d in vga_devices()))
    if name == "vga_capture":
        png, device, w, h, path = vga_grab(a.get("device"))
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
        LINE.open()
    except Exception as e:
        LINE.error = "serial did not open: %s" % e
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
    LINE.open()
    print("serial %s open; collecting two seconds..." % COM)
    time.sleep(2)
    print(text(LINE.take())[-500:] or "(nothing arrived)")

if __name__ == "__main__":
    if "--self-test" in sys.argv:
        self_test()
    else:
        serve()
