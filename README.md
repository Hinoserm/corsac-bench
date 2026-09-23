# The bench

The real machine's serial consoles and VGA capture belong to one program,
`CorsacBench.exe`, which lives in the Windows notification area. Every
assistant session shares it over MCP, and so does the person at the bench,
through its terminal and screen windows. A port opened by anyone is open
for everyone. Every session keeps its own place in each port's stream, so
what one session reads, another still gets.

```
 session ─ benchlink.py ─┐
 session ─ benchlink.py ─┼─ HTTP 127.0.0.1:7825/mcp ─ CorsacBench.exe ─┬─ COM5, COMn ...
 session ─ benchlink.py ─┘                            (tray icon)       ├─ terminal window
                                                                        └─ vgagrab.py ─ capture cards
```

| file | what it is |
| --- | --- |
| `app/` | CorsacBench.exe (C#, WinForms): the ports, the MCP endpoint, the tray icon, the terminal and the screens |
| `benchlink.py` | what a session runs: MCP on stdio, relayed to the bench; starts the bench if it is not running |
| `vgagrab.py` | the capture helper the bench keeps running: DirectShow through OpenCV |

## The windows

**Terminal** (click the tray icon). A tab for each port. It is an
xterm-compatible terminal:

- 256-colour and direct colour, DEC line drawing and the alternate screen,
  so nano and the like draw properly
- a 10,000-line scrollback
- select with the mouse; right-click, Ctrl+Shift+C or Ctrl+Insert copies
- Ctrl+Shift+V or Shift+Insert pastes
- Alt+key sends ESC+key (nano's M- commands); Backspace sends DEL

It shows everything the port receives, whoever is driving it. The toolbar
has:

- open and close
- line settings: speed, framing, flow control
- **Reset machine** (the kernel's `ESC ESC ESC RESET`)
- BREAK
- the terminal's size and text size

The status bar shows who opened the port, which session is running a
command on it, and what every session is doing.

**Screens**. Any or all of the capture devices, live. **Devices** picks
which. Double-click a picture to show it alone, and again to go back. A
picture's own settings are on its right-click menu. They are kept per
device in `bench.json`, and they say what happens when the machine changes
video mode:

| setting | choices |
| --- | --- |
| Capture size | **Follow the signal** (default): the card is asked for the input's resolution once a second, and the capture reopens at the new size when it changes, so 720x400 text, 640x480 and 1024x768 each arrive pixel for pixel. Or a fixed size from those the card offers, which the card scales to. |
| Shape | **Like a monitor**: every mode fills 4:3, as a CRT shows it. **Square pixels**. **Stretch to the window**. |
| Scale | **Fit the window**; **Whole multiples** (crisp text); **Actual size** |
| When the mode changes | **Resize the window to the new picture**, or **Keep the window** and refit the picture inside it |
| Trim black borders | crops even letterbox or pillarbox borders, e.g. at a fixed capture size. It takes effect once three frames agree, so a dark scene does not make the picture jump. |
| Frame rate, Smooth scaling, Show information | |

The menu also has pause, copy picture, save picture, **Use for
vga_capture**, and a list of the recent mode changes.

A device is opened only while something is looking at it (a visible view,
or a `vga_capture` within the last 20 seconds). Other programs can have it
the rest of the time.

## Tools

| tool | what it does |
| --- | --- |
| `serial_ports` | every COM port on the machine, which are open on the bench with what settings, and the default |
| `serial_open` | connect to a port (baud, bytesize, parity, stopbits, rtscts, xonxoff; 115200 8N1 by default) and make it the default, for everyone |
| `serial_close` | disconnect, for everyone, so another program can have the port |
| `serial_configure` | change a port's settings, live if it is open |
| `serial_default` | which port the other tools use when none is named |
| `serial_status` | settings, bytes received, bytes this session has not read, last error, the other sessions |
| `serial_read` | everything received since this session last looked, after collecting for `seconds` |
| `serial_send` | send text (newline appended unless `newline: false`) |
| `serial_wait` | block until `text` shows up, up to `seconds` |
| `serial_command` | run a shell command at a `# ` prompt and return its output; commands from different sessions take turns |
| `serial_login` | wait for `login:` and log in (root by default) |
| `serial_reset` | send `ESC ESC ESC RESET`; the kernel resets the machine from its serial interrupt |
| `serial_tail` | the last N characters, read or not |
| `serial_screen` | the terminal screen as the window shows it, with scrollback if asked |
| `bench_clients` | the sessions connected, and what each is doing |
| `vga_devices` | the capture devices, by index and name, with the default marked |
| `vga_select` | which capture device `vga_capture` uses by default |
| `vga_capture` | one frame at the signal's own resolution, as a PNG (also `C:\CORSAC\bench\vga.png`) |

Everything a port receives is appended to
`C:\CORSAC\bench\serial-<PORT>.log`, with what each session sent marked by
its name. The ports open at the last exit are opened again at start.

## Building and installing

It needs the .NET 10 SDK; the one in WSL builds it. The Windows side needs
the .NET 10 Desktop Runtime, and Python 3.9 or later with `opencv-python`
and `pygrabber` for the capture helper.

```
dotnet publish tools/bench/app -c Release -o /mnt/c/CORSAC/bench/app
```

Before publishing over a running copy, **Exit** it from the tray menu: a
running program's files cannot be replaced. It puts itself in the Run key
so it starts at login ("Start with Windows" on the tray menu). Settings are
in `C:\CORSAC\bench\bench.json`:

- `DefaultPort`, `Baud`, `Vga`
- `Listen` (7825)
- `Python`
- terminal size and font
- per-device screen settings

## Connecting a session

A session in WSL (`.mcp.json` here, or the assistant's user-level MCP
configuration for every project):

```json
{ "mcpServers": { "corsac-bench": { "command": "python3",
  "args": ["/home/hinoserm/projects/corsac86-kernel/tools/bench/benchlink.py"] } } }
```

The bridge talks to `http://127.0.0.1:7825/mcp`, which WSL's mirrored
networking reaches. If nothing answers, it starts
`C:\CORSAC\bench\app\CorsacBench.exe` and waits for it.

A Windows client uses the copy of the bridge beside the program:

```json
{ "mcpServers": { "corsac-bench": { "command": "C:\\Program Files\\Python39\\python.exe",
  "args": ["C:\\CORSAC\\bench\\app\\benchlink.py"] } } }
```

The desktop app's MCP file is its `*_desktop_config.json`. For the
Microsoft Store build it is under
`%LOCALAPPDATA%\Packages\<the app's package>\LocalCache\Roaming\<the app>\`, not
the plain `%APPDATA%`. Restart the app after changing it.

A client that speaks streamable HTTP can use `http://127.0.0.1:7825/mcp`
directly. `http://127.0.0.1:7825/status` is a plain-text summary.
