using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeoSwitch.App;

/// <summary>
/// Thin wrapper over <c>DwmSetWindowAttribute</c> so the main form can opt
/// into Windows 11 rounded corners and the Windows 10/11 dark title bar.
/// Every call is wrapped in a try/catch — pre-Win10 builds just silently no-op.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DwmInterop
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;  // pre-20H1
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE        = 20;  // 20H1+
    private const int DWMWA_WINDOW_CORNER_PREFERENCE       = 33;  // Win11
    private const int DWMWA_BORDER_COLOR                   = 34;  // Win11

    private const int DWMWCP_DEFAULT    = 0;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND      = 2;
    private const int DWMWCP_ROUNDSMALL = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int valueSize);

    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        int one = 1;
        // Try the modern attribute first, then the legacy one for Win10 pre-20H1.
        TrySet(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE,        ref one);
        TrySet(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref one);
    }

    public static void UseRoundedCorners(IntPtr hwnd, bool small = false)
    {
        int v = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
        TrySet(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref v);
    }

    public static void SetBorderColor(IntPtr hwnd, int bgr)
    {
        TrySet(hwnd, DWMWA_BORDER_COLOR, ref bgr);
    }

    private static void TrySet(IntPtr hwnd, int attr, ref int value)
    {
        try { _ = DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int)); }
        catch { /* DLL/flag unsupported on this Windows version */ }
    }
}
