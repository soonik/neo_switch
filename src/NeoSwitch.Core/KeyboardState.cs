using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NeoSwitch.Core;

/// <summary>
/// <c>GetAsyncKeyState</c>-based snapshot of physical keyboard state.
/// Covers modifiers, alphanumerics, function / navigation / numpad and OEM
/// punctuation keys — everything that could be held and lose its key-up if
/// the firmware resets its matrix mid-switch.
///
/// Mouse buttons, IME / Hangul keys, browser-and-media VKs and
/// Print-Screen/Pause are intentionally excluded either because they can't
/// "stick" meaningfully or because <c>GetAsyncKeyState</c> doesn't report
/// their state reliably.
/// </summary>
[SupportedOSPlatform("windows")]
public static class KeyboardState
{
    /// <summary>Are any "watched" keys currently held down?</summary>
    public static bool AnyKeyHeld()
    {
        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            if (!IsWatched(vk)) continue;
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        }
        return false;
    }

    /// <summary>Short human-readable description of held keys — for logs and UI.</summary>
    public static string HeldDescription()
    {
        var parts = new List<string>();
        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            if (!IsWatched(vk)) continue;
            if ((GetAsyncKeyState(vk) & 0x8000) == 0) continue;
            parts.Add(VkName(vk));
            if (parts.Count >= 8) { parts.Add("…"); break; }
        }
        return parts.Count == 0 ? "(none)" : string.Join("+", parts);
    }

    /// <summary>
    /// Allowlist of VKs that can plausibly be held during an alt-tab gesture
    /// and whose stuck-key effects would be visible to the user.
    /// </summary>
    private static bool IsWatched(int vk)
    {
        // Alphanumerics
        if (vk >= 0x30 && vk <= 0x39) return true;   // 0-9
        if (vk >= 0x41 && vk <= 0x5A) return true;   // A-Z
        // Function keys F1-F24
        if (vk >= 0x70 && vk <= 0x87) return true;
        // Numpad (digits, operators)
        if (vk >= 0x60 && vk <= 0x6F) return true;
        // Navigation cluster + arrows
        if (vk >= 0x21 && vk <= 0x28) return true;   // PgUp/Dn End Home arrows
        // L/R modifiers (0xA0-0xA5 mirror 0x10-0x12 on most keyboards,
        // but some only report these so include both)
        if (vk >= 0xA0 && vk <= 0xA5) return true;

        switch (vk)
        {
            case 0x08:                   // Backspace
            case 0x09:                   // Tab
            case 0x0D:                   // Enter
            case 0x10: case 0x11: case 0x12:  // Shift/Ctrl/Alt (generic)
            case 0x14:                   // CapsLock
            case 0x1B:                   // Esc
            case 0x20:                   // Space
            case 0x2D:                   // Insert
            case 0x2E:                   // Delete
            case 0x5B: case 0x5C:        // L/R Win
            case 0x5D:                   // Apps (menu key)
            case 0x90:                   // NumLock
            case 0x91:                   // ScrollLock
                return true;
        }

        // OEM punctuation: 0xBA..0xC0 (;=,-./`) and 0xDB..0xE2 ([\]' ISO slash)
        if (vk >= 0xBA && vk <= 0xC0) return true;
        if (vk >= 0xDB && vk <= 0xE2) return true;

        return false;
    }

    private static string VkName(int vk) => vk switch
    {
        0x08 => "Back", 0x09 => "Tab", 0x0D => "Enter",
        0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt",
        0x14 => "Caps", 0x1B => "Esc", 0x20 => "Space",
        0x2D => "Ins", 0x2E => "Del",
        0x5B => "LWin", 0x5C => "RWin", 0x5D => "Apps",
        0x90 => "NumLk", 0x91 => "ScrLk",
        >= 0x21 and <= 0x28 => new[] { "PgUp", "PgDn", "End", "Home", "Left", "Up", "Right", "Down" }[vk - 0x21],
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",
        >= 0x70 and <= 0x87 => $"F{vk - 0x70 + 1}",
        0xA0 => "LShift", 0xA1 => "RShift",
        0xA2 => "LCtrl",  0xA3 => "RCtrl",
        0xA4 => "LAlt",   0xA5 => "RAlt",
        _ => $"VK{vk:X2}",
    };

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
