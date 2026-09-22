# The bench

`corsacbench.py` puts the real machine's serial console and its VGA capture
behind MCP tools, so an assistant can drive the board: send commands, wait
for output, reset it with the kernel's serial magic sequence, and look at
the screen.

It runs on the Windows side, where COM5 and the DirectShow capture cards
are. It needs Python 3.9 or later with `pyserial`, `opencv-python` and
`pygrabber`; it speaks MCP over stdio itself and needs no SDK.

## Tools

| tool | what it does |
| --- | --- |
| `serial_ports` | every COM port on the machine, which are open here with what settings, and the default |
| `serial_open` | connect to a port (baud, bytesize, parity, stopbits, rtscts, xonxoff; 115200 8N1 by default) and make it the default |
| `serial_close` | disconnect, so another program can have the port |
| `serial_configure` | change a port's settings, live if it is open |
| `serial_default` | which port the other tools use when none is named |
| `serial_status` | settings, bytes received, unread bytes, last error |
| `serial_read` | everything received since the last look, after collecting for `seconds` |
| `serial_send` | send text (newline appended unless `newline: false`) |
| `serial_wait` | block until `text` shows up, up to `seconds` |
| `serial_command` | run a shell command at a `# ` prompt and return its output |
| `serial_login` | wait for `login:` and log in (root by default) |
| `serial_reset` | send `ESC ESC ESC RESET`; the kernel resets the machine from its serial interrupt |
| `serial_tail` | the last N characters, read or not |
| `vga_devices` | the capture devices, by DirectShow index and name, with the default marked |
| `vga_select` | which capture device `vga_capture` uses by default |
| `vga_capture` | one frame of the screen as a PNG (also `C:\CORSAC\bench\vga.png`) |

Every serial tool takes an optional `port`; any number of ports can be open
at once, each with its own log.
Everything a port receives is appended to `C:\CORSAC\bench\serial-<PORT>.log`
from the moment it is opened; the default port is opened when the server starts.

## The code assistant (this repository)

`.mcp.json` at the repository root registers the server; a WSL session
starts the Windows Python directly. The serial port must not be open in a
terminal program at the same time -- Windows gives a COM port to one
process only.

## The desktop app (Windows)

Add to the desktop app's MCP configuration file (`*_desktop_config.json`).
For the Microsoft Store build of the app that file is not under the plain
`%APPDATA%` but in the package's virtualized copy of it, under
`%LOCALAPPDATA%\Packages\<the app's package>\LocalCache\Roaming\`; a file
written to the plain path is never read. The app has to be restarted to
notice a change:

```json
{
  "mcpServers": {
    "corsac-bench": {
      "command": "C:\\Program Files\\Python39\\python.exe",
      "args": ["\\\\wsl.localhost\\FedoraLinux-43\\home\\hinoserm\\projects\\corsac86-kernel\\tools\\bench\\corsacbench.py"],
      "env": { "CORSAC_COM": "COM5", "CORSAC_VGA": "00-1 Pro Capture Dual DVI" }
    }
  }
}
```

## Settings

`CORSAC_COM` (COM5), `CORSAC_BAUD` (115200), `CORSAC_VGA` (a substring of
the capture device's name), `CORSAC_DIR` (`C:\CORSAC\bench`).

`python corsacbench.py --self-test` lists the devices, grabs a frame and
listens on the port for two seconds.
