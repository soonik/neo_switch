using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeoSwitch.Core;

/// <summary>
/// Minimal <c>GetAsyncKeyState</c> wrapper for reading the current
/// physical-key down state of modifier keys.
///
/// Used by <see cref="DebouncedSwitcher"/> to defer the HID write until no
/// modifier is held. Rationale: when the firmware processes a profile-switch
/// command it briefly pauses the matrix scan, and any key held at that moment
/// can lose its key-up event. Non-modifier key-drops self-correct on the next
/// press; modifier drops leave the whole OS in a "stuck Alt/Ctrl" state.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ModifierKeys
{
    private const int VK_SHIFT   = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU    = 0x12;   // Alt
    private const int VK_LWIN    = 0x5B;
    private const int VK_RWIN    = 0x5C;

    /// <summary>Any of Shift / Ctrl / Alt / L-Win / R-Win physically held down.</summary>
    public static bool AnyHeld() =>
        IsHeld(VK_SHIFT) || IsHeld(VK_CONTROL) || IsHeld(VK_MENU)
        || IsHeld(VK_LWIN) || IsHeld(VK_RWIN);

    public static string HeldString()
    {
        var parts = new List<string>(5);
        if (IsHeld(VK_MENU))    parts.Add("Alt");
        if (IsHeld(VK_CONTROL)) parts.Add("Ctrl");
        if (IsHeld(VK_SHIFT))   parts.Add("Shift");
        if (IsHeld(VK_LWIN))    parts.Add("LWin");
        if (IsHeld(VK_RWIN))    parts.Add("RWin");
        return parts.Count == 0 ? "(none)" : string.Join("+", parts);
    }

    private static bool IsHeld(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
