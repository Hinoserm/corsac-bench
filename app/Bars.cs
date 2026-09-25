// Tool bars that take the first click, and Windows' own icons for them.

using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CorsacBench;

/// A WinForms tool strip swallows the click that activates its window, so a
/// button on an inactive window needs clicking twice. This one lets the
/// click through: the window activates and the button is pressed.
public class ClickThroughStrip : ToolStrip
{
    const int WM_MOUSEACTIVATE = 0x0021, MA_ACTIVATE = 1, MA_ACTIVATEANDEAT = 2;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_MOUSEACTIVATE && m.Result == MA_ACTIVATEANDEAT) m.Result = MA_ACTIVATE;
    }
}

/// The same, for a status strip.
public class ClickThroughStatusStrip : StatusStrip
{
    const int WM_MOUSEACTIVATE = 0x0021, MA_ACTIVATE = 1, MA_ACTIVATEANDEAT = 2;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_MOUSEACTIVATE && m.Result == MA_ACTIVATEANDEAT) m.Result = MA_ACTIVATE;
    }
}

/// An icon from a Windows resource library, where every version since 7 keeps it.
public readonly record struct WinIcon(string Dll, int Index)
{
    public static readonly WinIcon Camera = new("imageres.dll", 52);
    public static readonly WinIcon Monitor = new("imageres.dll", 104);
    public static readonly WinIcon Play = new("imageres.dll", 281);
    public static readonly WinIcon Save = new("shell32.dll", 258);
    public static readonly WinIcon Stop = new("wmploc.dll", 135);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[]? ids, int count, int flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);

    /// The icon drawn by Windows at `size` pixels, or null if this Windows lacks it.
    public Bitmap? Load(int size)
    {
        var handles = new IntPtr[1];
        string path = Path.Combine(Environment.SystemDirectory, Dll);
        if (PrivateExtractIcons(path, Index, size, size, handles, null, 1, 0) != 1 || handles[0] == IntPtr.Zero) return null;
        try
        {
            using var icon = Icon.FromHandle(handles[0]);
            return icon.ToBitmap();
        }
        finally { DestroyIcon(handles[0]); }
    }
}
