# corsac-bench

A test bench for a real machine, on one Windows PC: its serial consoles and
its video capture cards, owned by one program in the notification area and
shared with any number of AI assistant sessions over
[MCP](https://modelcontextprotocol.io), and with you, through terminal and
screen windows on the desktop.

It was written to develop an operating system on real 486 and Pentium
hardware, where the only ways to talk to the machine are a serial cable and
a VGA capture card, and several assistant sessions and a person all want
them at once. Windows gives a COM port to one process only; this is that
process, and everybody else goes through it.

**Low-latency capture on Magewell Pro Capture cards.** Magewell cards are
read through Magewell's own SDK in its low-latency mode, not DirectShow. Each
frame is drawn in stripes as it arrives, through Direct3D with tearing
allowed, so the picture on the desktop trails the machine by a fraction of a
frame. That is fast enough to play games through. Every other capture device
works through DirectShow. See [Magewell Pro Capture cards](#magewell-pro-capture-cards).

```
 session ─ benchlink.py ─┐
 session ─ benchlink.py ─┼─ HTTP 127.0.0.1:7825/mcp ─ CorsacBench.exe ─┬─ COM ports
 session ─ benchlink.py ─┘                            (tray icon)       ├─ terminal and screen windows
                                                                        ├─ LibMWCapture.dll ─ Magewell cards (low latency)
                                                                        └─ vgagrab.py ─ other capture cards
```

## What it does

**Serial ports**
- Any number open at once, shared: a port opened or closed by anyone is
  opened or closed for everyone.
- Every session keeps its own place in each port's stream, so what one reads
  another still gets.
- Commands from different sessions take turns on a port instead of
  interleaving.
- Every byte a port receives also goes to an always-on log,
  `serial-<PORT>.log`.
- **Recording** to files:
  - any number at once, of one port or several into one file
  - raw bytes, plain text (escape sequences removed), or text with a
    timestamp on every line
  - optionally with what was sent, marked with who sent it
  - running recordings resume after a restart
- RTS/CTS and XON/XOFF, any speed and framing, BREAK, and a configurable
  reset sequence for machines that watch their serial line for one.

**Terminal windows**
- As many as you like, each with a tab per port, remembered across restarts.
- An xterm-compatible terminal with the whole VT100:
  - 256-colour and direct colour, line drawing, the alternate screen
  - double-width and double-height lines, 132 columns, VT52 mode
  - the application keypad
  - a 10,000-line scrollback, selection, copy and paste
- The window you type in is the same terminal the sessions see:
  `serial_screen` returns exactly what it shows.

**Screen windows**
- Any or all DirectShow capture devices, live, in as many windows as you like.
- By default the capture follows the input signal's own resolution, so text
  mode and every graphics mode arrive pixel for pixel. This works with cards
  that report the input resolution as their first format, such as Magewell
  Pro Capture.
- Each device has settings for when the video mode changes:
  - monitor 4:3, square pixels, or stretch
  - fit, whole multiples, or actual size
  - keep the window's size and place and rescale the picture inside it (the
    default), or resize the window to the new picture
  - black-border trimming, frame rate, smoothing
- `vga_capture` gives a session one frame as a PNG.
- `vga_capture_series` gives a session a whole span of time in one image,
  for example 4 frames over a minute to time a boot.

### Magewell Pro Capture cards

Magewell Pro Capture cards skip DirectShow and Python altogether. They use
the card's own SDK (`LibMWCapture.dll`, which the Magewell driver installs) in
its low-latency mode:
- the card writes each frame into the bench's memory in 64-line stripes while
  the frame is still arriving
- Direct3D 11 draws each stripe as it lands, on a thread of its own, through a
  flip-model swap chain that holds at most one frame
- by default the picture is presented without waiting for the display's
  refresh, and can tear; *Presentation > Synchronised to the display* removes
  tearing at the cost of up to one refresh
- pixels are copied untouched: BGRA straight through, no deinterlacing, no
  aspect or colour conversion, point sampling unless smoothing is on
- frames always arrive at the input's own resolution; a mode change reopens
  the channel at the new size the moment the card reports it
- analog inputs carry no pixel clock, so one sync can fit several timings
  (720x400 text and 640x400 graphics are the same sync). The card lists
  every timing that fits, at any resolution the card supports, and the bench
  tries them and keeps the one whose pixels come out crisp. Sampling at the
  wrong clock smears pixel edges between samples, and the bench measures
  that on the live picture. It remembers the choice for each sync, and judges
  again if the machine changes mode within the same sync.
- the information bar shows the signal's refresh rate, the analog timing in
  use, the capture rate, the
  card's own latency (the frame's first line on the wire to the whole frame
  in memory) and the time from there to the display

Every other device stays on the DirectShow path. The SDK's headers,
libraries, documents and low-latency examples are in
`third_party/magewell-capture-sdk-3.3.1.1596`.

## Requirements

- Windows 10 or 11, with the [.NET 10 Desktop
  Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
- For Magewell cards: the Magewell Pro Capture driver.
- For other capture devices: Python 3.9 or later with `opencv-python` and `pygrabber`
  (`pip install opencv-python pygrabber`). Serial works without it.
- For the bridge: any Python 3.8 or later, on Windows or in WSL. It uses the
  standard library only.

## Install

Download a release, or build it with the .NET 10 SDK, on Windows or in WSL:

```
dotnet publish app -c Release -o <folder>
```

The output folder contains `CorsacBench.exe`, `vgagrab.py` and
`benchlink.py`. Run `CorsacBench.exe`. It puts itself in the notification
area, and in the Run key so it starts at login ("Start with Windows" on its
menu). Starting it again brings its windows forward. `--screens` opens the
screen windows.

## Connect a session

Any MCP client that runs a command runs the bridge:

```json
{ "mcpServers": { "corsac-bench": {
    "command": "python",
    "args": ["C:\\path\\to\\benchlink.py"] } } }
```

From WSL, use `python3` and the path in WSL. With WSL's mirrored networking,
127.0.0.1 reaches the bench. The bridge starts `CorsacBench.exe` if nothing
answers. It looks for it in `CORSAC_BENCH_EXE`, then beside itself, then in
`C:\CORSAC\bench\app`, then `%LOCALAPPDATA%\corsac-bench\app`.

A client that speaks streamable HTTP can use `http://127.0.0.1:7825/mcp`
directly. `http://127.0.0.1:7825/status` is a plain-text summary.

## Tools

| tool | what it does |
| --- | --- |
| `serial_ports` | every COM port, which are open on the bench with what settings, and the default |
| `serial_open` / `serial_close` | open or close a port, for everyone |
| `serial_configure` | speed, framing and flow control, live |
| `serial_default` | which port the tools use when none is named |
| `serial_status` | settings, bytes received and unread, errors, the other sessions |
| `serial_read` | what arrived since this session last looked |
| `serial_send` | send text |
| `serial_wait` | wait until some text arrives |
| `serial_command` | run a shell command at the prompt and return its output |
| `serial_login` | wait for `login:` and log in |
| `serial_reset` | send the reset sequence and wait for the boot banner |
| `serial_tail` | the last N characters, read or not |
| `serial_screen` | the terminal screen as drawn, with scrollback |
| `serial_record_start` / `_stop` / `_list` | record ports to files |
| `bench_clients` | the connected sessions and what each is doing |
| `vga_devices` / `vga_select` / `vga_capture` | the capture devices, and a frame from one |
| `vga_capture_series` | frames taken evenly over a span of time, in one image in time order, each timed beneath; several can run at once alongside every other tool |

## Settings

`bench.json` lives in `CORSAC_DIR` if that is set. Otherwise it is in
`C:\CORSAC\bench` if that exists, and otherwise in
`%LOCALAPPDATA%\corsac-bench`. The logs, recordings and `vga.png` go in the
same folder. Most settings are set from the windows. The rest:

| setting | default | |
| --- | --- | --- |
| `Listen` | 7825 | the MCP port on 127.0.0.1 |
| `DefaultPort` | COM1 | |
| `OpenAtStart` | none | kept as ports are opened and closed |
| `Vga` | the first device | a part of the default capture device's name |
| `Python` | found on PATH | for the capture helper |
| `WslDistro` | Windows' default | where recording paths like `/home/...` point |
| `ResetSequence` | `ESC ESC ESC RESET` | what the reset tool and button send |
| `ResetBanner` | `CORSAC boot` | what the reset tool waits for |
| `ShellPrompt` | `# ` | what `serial_command` and `serial_login` wait for |

## License

MIT; see [LICENSE](LICENSE).

The MIT license covers this project's own code only. We claim no ownership
of the included Magewell components. They belong to Magewell and remain
under Magewell's own terms.
