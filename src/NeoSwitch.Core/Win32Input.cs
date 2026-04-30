using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeoSwitch.Core;

/// <summary>
/// <c>SendInput</c> wrapper used to forge synthetic <b>key-up</b> events when
/// the firmware drops a real one mid-profile-switch (the classic "stuck key"
/// after <c>D0 B1</c>). We never synthesise key-down — only key-up — so the
/// worst case if a key is genuinely still held is one frame of release before
/// the keyboard's next scan re-reports the down event.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Win32Input
{
    /// <summary>
    /// Send <c>KEYEVENTF_KEYUP</c> for every VK in <paramref name="vks"/> that
    /// is still showing as held. Returns the number of inputs actually sent.
    /// Skips keys that have since been released by the user (or by the
    /// firmware) so we don't spam the OS with no-ops.
    /// </summary>
    public static int ReleaseHeldKeys(IReadOnlyCollection<int> vks)
    {
        if (vks == null || vks.Count == 0) return 0;
        var inputs = new List<INPUT>(vks.Count);
        foreach (int vk in vks)
        {
            if (!KeyboardState.IsHeld(vk)) continue;
            inputs.Add(MakeKeyUp(vk));
        }
        if (inputs.Count == 0) return 0;
        var arr = inputs.ToArray();
        return (int)SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Sweep every "watched" VK and synthesise a <c>KEYUP</c> for each one
    /// the OS thinks is still held. Used by the tray "Release stuck keys"
    /// command — user-initiated, so disrupting a real held key is acceptable.
    /// </summary>
    public static int ReleaseAllHeld() => ReleaseHeldKeys(KeyboardState.GetHeldVks());

    // -------- internals --------

    private static INPUT MakeKeyUp(int vk)
    {
        uint flags = KEYEVENTF_KEYUP;
        if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        ushort scan = (ushort)MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        return new INPUT
        {
            Type = INPUT_KEYBOARD,
            Data = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    Vk = (ushort)vk,
                    Scan = scan,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            },
        };
    }

    private static bool IsExtendedKey(int vk) => vk switch
    {
        // Navigation cluster + arrow keys
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 => true,
        0x2D or 0x2E => true,          // Insert / Delete
        0x5B or 0x5C or 0x5D => true,  // L-Win / R-Win / Apps
        0x90 => true,                  // NumLock
        0x6F => true,                  // Numpad /
        0xA3 or 0xA5 => true,          // R-Ctrl / R-Alt
        _ => false,
    };

    // -------- P/Invoke --------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private const uint INPUT_KEYBOARD        = 1;
    private const uint KEYEVENTF_KEYUP       = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint MAPVK_VK_TO_VSC       = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint Data;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }
}
