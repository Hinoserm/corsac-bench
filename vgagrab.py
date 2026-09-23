#!/usr/bin/env python3
"""The bench's eyes: DirectShow capture devices, for CorsacBench.exe.

The bench app starts this once and keeps it; each device it is asked about is
opened, read continuously by a thread, and closed again when nobody has asked
for a frame for a while, so another program can have it.

  python vgagrab.py serve     # requests on stdin, one JSON line each
  python vgagrab.py devices   # one JSON line: the devices
  python vgagrab.py grab <device name or part of it> <out.png>

Requests and replies are one JSON line each; a frame reply is followed by
`len` bytes of raw 24-bit BGR, `width` * `height` * 3, rows top down.

  {"op": "devices"}
  {"op": "formats", "device": name}          -> the signal's size and every size offered
  {"op": "open", "device": name, "width": w, "height": h, "follow": true}
  {"op": "close", "device": name}
  {"op": "frame", "device": name, "since": seq, "wait": seconds}
  {"op": "status"}

With `follow` the device is captured at the size of the signal coming in and
reopened whenever that changes. The Magewell Pro Capture cards offer the
input's own resolution as their first format, and a query of it costs ~50 ms,
so it is polled once a second. Python 3.9, opencv-python, pygrabber.
"""
import json, sys, threading, time

import cv2
import pygrabber.dshow_graph as dg
import pygrabber.dshow_ids as ids


class Subtypes(dict):
    """pygrabber dies on media subtypes it has no name for; the cards offer dozens."""
    def __missing__(self, key):
        return "unknown"


dg.subtypes = ids.subtypes = Subtypes(ids.subtypes)

IDLE = 20.0          # seconds without a frame request before a device is let go
WARM = 6             # frames thrown away after opening: DirectShow's first are dark or torn


def devices():
    return dg.FilterGraph().get_input_devices()


def index_of(name):
    names = devices()
    for i, d in enumerate(names):
        if d == name:
            return i, d
    for i, d in enumerate(names):
        if name.lower() in d.lower():
            return i, d
    raise ValueError("no capture device matches %r; have %s" % (name, names))


def formats(index):
    g = dg.FilterGraph()
    g.add_video_input_device(index)
    sizes = []
    for f in g.get_input_device().get_formats():
        s = (f["width"], f["height"])
        if s not in sizes:
            sizes.append(s)
    return sizes


class Device:
    def __init__(self, name):
        self.index, self.name = index_of(name)
        self.lock = threading.Condition()
        self.frame = None
        self.seq = 0
        self.native = None
        self.want = None            # (w, h) asked for, or None to follow the signal
        self.size = None            # what the device is delivering
        self.error = ""
        self.asked = time.time()
        self.fps = 0.0
        self.running = False

    def start(self, want):
        self.want = want
        if not self.running:
            self.running = True
            threading.Thread(target=self.run, daemon=True).start()
        else:
            with self.lock:
                self.size = None    # the reader reopens at the new size

    def stop(self):
        self.running = False

    def target(self):
        if self.want:
            return self.want
        try:
            self.native = formats(self.index)[0]
        except Exception as e:
            self.error = "could not ask the signal's size: %s" % e
        return self.native or (1024, 768)

    def run(self):
        cap = None
        polled = 0.0
        count, counted = 0, time.time()
        try:
            while self.running:
                if time.time() - self.asked > IDLE:
                    break
                now = time.time()
                if cap is None or self.size is None or (self.want is None and now - polled > 1.0):
                    polled = now
                    size = self.target()
                    if cap is None or size != self.size:
                        if cap is not None:
                            cap.release()
                        cap = cv2.VideoCapture(self.index, cv2.CAP_DSHOW)
                        if not cap.isOpened():
                            self.error = "could not open %s" % self.name
                            cap = None
                            time.sleep(2)
                            continue
                        cap.set(cv2.CAP_PROP_FRAME_WIDTH, size[0])
                        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, size[1])
                        for _ in range(WARM):
                            cap.read()
                        self.size = size
                        self.error = ""
                ok, frame = cap.read()
                if not ok:
                    self.error = "no frame from %s" % self.name
                    time.sleep(0.2)
                    continue
                with self.lock:
                    self.frame = frame
                    self.seq += 1
                    self.lock.notify_all()
                count += 1
                if now - counted >= 1.0:
                    self.fps = count / (now - counted)
                    count, counted = 0, now
        finally:
            if cap is not None:
                cap.release()
            self.running = False
            with self.lock:
                self.lock.notify_all()


OPEN = {}


def device(name, want=None, start=True):
    d = OPEN.get(name)
    if d is None:
        d = Device(name)
        OPEN[name] = d
    if start and not d.running:
        d.asked = time.time()
        d.start(d.want if want is None else want)
    return d


def serve():
    out = sys.stdout.buffer
    for raw in sys.stdin.buffer:
        raw = raw.strip()
        if not raw:
            continue
        data = b""
        try:
            req = json.loads(raw)
            op = req.get("op")
            if op == "devices":
                reply = {"devices": devices()}
            elif op == "formats":
                i, name = index_of(req["device"])
                sizes = formats(i)
                reply = {"device": name, "native": sizes[0] if sizes else None, "sizes": sizes}
            elif op == "open":
                _, name = index_of(req["device"])
                want = None if req.get("follow", True) else (int(req["width"]), int(req["height"]))
                d = device(name, start=False)
                d.asked = time.time()
                d.start(want)
                reply = {"device": name}
            elif op == "close":
                d = OPEN.pop(index_of(req["device"])[1], None)
                if d:
                    d.stop()
                reply = {"closed": True}
            elif op == "frame":
                _, name = index_of(req["device"])
                d = device(name)
                d.asked = time.time()
                since = int(req.get("since", 0))
                end = time.time() + float(req.get("wait", 5))
                with d.lock:
                    while d.seq <= since and d.running and time.time() < end:
                        d.lock.wait(max(0.01, end - time.time()))
                    frame, seq = d.frame, d.seq
                if frame is None:
                    raise RuntimeError(d.error or "no frame yet from %s" % name)
                data = frame.tobytes()
                reply = {"device": name, "seq": seq, "width": frame.shape[1], "height": frame.shape[0],
                         "native": d.native, "fps": round(d.fps, 1), "follow": d.want is None, "len": len(data)}
            elif op == "status":
                reply = {"open": [{"device": d.name, "running": d.running, "size": d.size, "native": d.native,
                                   "fps": round(d.fps, 1), "error": d.error} for d in OPEN.values()]}
            else:
                raise ValueError("unknown op %r" % op)
        except Exception as e:
            reply, data = {"error": str(e)}, b""
        out.write((json.dumps(reply) + "\n").encode())
        if data:
            out.write(data)
        out.flush()


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "serve":
        serve()
    elif len(sys.argv) > 1 and sys.argv[1] == "devices":
        print(json.dumps({"devices": devices()}))
    elif len(sys.argv) > 3 and sys.argv[1] == "grab":
        d = device(index_of(sys.argv[2])[1])
        with d.lock:
            while d.seq < 1 and d.running:
                d.lock.wait(1)
        d.stop()
        if d.frame is None:
            print(json.dumps({"error": d.error or "no frame"}))
            return
        cv2.imwrite(sys.argv[3], d.frame)
        print(json.dumps({"device": d.name, "width": d.frame.shape[1], "height": d.frame.shape[0], "path": sys.argv[3]}))
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
