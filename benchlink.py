#!/usr/bin/env python3
"""An assistant session's line to the bench: MCP on stdin/stdout, relayed to
the one CorsacBench.exe every session shares, over HTTP on 127.0.0.1.

If the bench is not running it is started, from Windows or from WSL alike,
and the request waits for it. It holds no port and no state of its own
beyond the session id the bench hands out, so any number of these can run.
The standard library only; any Python 3.8 or later, Windows or Linux.

  CORSAC_BENCH_URL  http://127.0.0.1:7825/mcp
  CORSAC_BENCH_EXE  C:\\CORSAC\\bench\\app\\CorsacBench.exe
"""
import http.client, json, os, subprocess, sys, threading, time, urllib.parse, urllib.request

URL = os.environ.get("CORSAC_BENCH_URL", "http://127.0.0.1:7825/mcp")
EXE = os.environ.get("CORSAC_BENCH_EXE", r"C:\CORSAC\bench\app\CorsacBench.exe")

session = None
out_lock = threading.Lock()
start_lock = threading.Lock()


def windows_path(p):
    """The bench's path as this side of WSL can run it."""
    if os.name == "nt":
        return p
    drive, rest = p[0].lower(), p[2:].replace("\\", "/")
    return "/mnt/%s%s" % (drive, rest)


def start_bench():
    with start_lock:
        if reachable():
            return
        exe = windows_path(EXE)
        flags = 0x00000008 | 0x00000200 if os.name == "nt" else 0     # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
        subprocess.Popen([exe], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         cwd=os.path.dirname(exe), creationflags=flags, start_new_session=os.name != "nt")
        end = time.time() + 30
        while time.time() < end and not reachable():
            time.sleep(0.25)


def reachable():
    try:
        urllib.request.urlopen(URL.rsplit("/", 1)[0] + "/status", timeout=1).read()
        return True
    except Exception:
        return False


def post(body):
    """One request. The connect has its own short limit: under WSL's mirrored
    networking a connect to a loopback port nobody listens on hangs rather
    than being refused. The answer may take as long as a tool call does."""
    global session
    u = urllib.parse.urlsplit(URL)
    headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    if session:
        headers["Mcp-Session-Id"] = session
    c = http.client.HTTPConnection(u.hostname, u.port or 80, timeout=3)
    try:
        c.connect()
        c.sock.settimeout(3600)
        c.request("POST", u.path or "/", body=body, headers=headers)
        r = c.getresponse()
        sid = r.getheader("Mcp-Session-Id")
        if sid:
            session = sid
        return r.status, r.read()
    finally:
        c.close()


def reply(obj):
    with out_lock:
        sys.stdout.buffer.write(json.dumps(obj).encode() + b"\n")
        sys.stdout.buffer.flush()


def relay(raw):
    try:
        msg = json.loads(raw)
    except ValueError:
        return
    ident = msg.get("id") if isinstance(msg, dict) else None
    for attempt in range(2):
        try:
            status, data = post(raw)
            if status >= 400:
                if ident is not None:
                    reply({"jsonrpc": "2.0", "id": ident, "error": {"code": -32603, "message": "bench said HTTP %d" % status}})
                return
            if status == 200 and data.strip():
                with out_lock:
                    sys.stdout.buffer.write(data.strip() + b"\n")
                    sys.stdout.buffer.flush()
            return
        except OSError as e:
            if attempt == 0:
                start_bench()
                continue
            if ident is not None:
                reply({"jsonrpc": "2.0", "id": ident, "error": {"code": -32603,
                       "message": "the bench (%s) is not answering and could not be started: %s" % (EXE, e)}})


def main():
    first = True
    calls = []
    for raw in sys.stdin.buffer:
        raw = raw.strip()
        if not raw:
            continue
        if first:
            # initialize is answered before anything else is sent, so it
            # sets the session id the rest use.
            first = False
            relay(raw)
            continue
        t = threading.Thread(target=relay, args=(raw,), daemon=True)
        t.start()
        calls = [c for c in calls if c.is_alive()] + [t]
    # The client has gone; answers still owed are finished first, then the
    # bench is told this session is over.
    for c in calls:
        c.join()
    if session:
        try:
            u = urllib.parse.urlsplit(URL)
            c = http.client.HTTPConnection(u.hostname, u.port or 80, timeout=3)
            c.request("DELETE", u.path or "/", headers={"Mcp-Session-Id": session})
            c.getresponse().read()
            c.close()
        except OSError:
            pass


if __name__ == "__main__":
    main()
