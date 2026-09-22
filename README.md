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
| `serial_status` | port, bytes received, unread bytes, last error |
| `serial_read` | everything received since the last look, after collecting for `seconds` |
| `serial_send` | send text (newline appended unless `newline: false`) |
| `serial_wait` | block until `text` shows up, up to `seconds` |
| `serial_command` | run a shell command at a `# ` prompt and return its output |
| `serial_login` | wait for `login:` and log in (root by default) |
| `serial_reset` | send `ESC ESC ESC RESET`; the kernel resets the machine from its serial interrupt |
| `serial_tail` | the last N characters, read or not |
| `vga_devices` | the capture devices, by DirectShow index and name |
| `vga_capture` | one frame of the screen as a PNG (also `C:\CORSAC\bench\vga.png`) |

Everything the machine says is appended to `C:\CORSAC\bench\serial.log`
from the moment the server starts.

## The code assistant (this repository)

`.mcp.json` at the repository root registers the server; a WSL session
starts the Windows Python directly. The serial port must not be open in a
terminal program at the same time -- Windows gives a COM port to one
process only.

## The desktop app (Windows)

Add to the desktop app's MCP configuration file under `%APPDATA%`:

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
