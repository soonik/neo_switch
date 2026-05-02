using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeoSwitch.App;

/// <summary>
/// Modifier flags accepted by <see cref="HotkeyHost.Register"/> (and the
/// underlying Win32 <c>RegisterHotKey</c>). Combine with bitwise OR.
/// </summary>
[Flags]
public enum HotkeyMods : uint
{
    None    = 0x0000,
    Alt     = 0x0001,
    Control = 0x0002,
    Shift   = 0x0004,
    Win     = 0x0008,
    /// <summary>Without this, holding the chord auto-repeats the WM_HOTKEY.</summary>
    NoRepeat = 0x4000,
}

/// <summary>
/// Hidden message-only window that owns global hotkeys via <c>RegisterHotKey</c>.
/// This is the standard, anti-cheat-friendly way to add a system-wide hotkey
/// on Windows — not a global keyboard hook, just a notification subscription.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HotkeyHost : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HWND_MESSAGE = -3;

    private readonly Dictionary<int, Action> _byId = new();
    private int _nextId = 0xC100;   // app-private range, not 0xC000+ so we don't clash with shared atoms

    public HotkeyHost()
    {
        var cp = new CreateParams
        {
            Caption = "NeoSwitch.HotkeyHost",
            // Message-only window — no UI, no taskbar entry, just a HWND.
            Parent = (IntPtr)HWND_MESSAGE,
        };
        CreateHandle(cp);
    }

    /// <summary>
    /// Bind <paramref name="action"/> to the given hotkey. Returns the registration
    /// id. Throws <see cref="InvalidOperationException"/> if Windows refuses (most
    /// commonly because the chord is already taken by another app).
    /// </summary>
    public int Register(HotkeyMods modifiers, uint vk, Action action)
    {
        int id = _nextId++;
        if (!RegisterHotKey(Handle, id, (uint)(modifiers | HotkeyMods.NoRepeat), vk))
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"RegisterHotKey failed (Win32 error {err}). The chord is probably already in use.");
        }
        _byId[id] = action;
        return id;
    }

    public void Unregister(int id)
    {
        if (!_byId.Remove(id)) return;
        UnregisterHotKey(Handle, id);
    }

    public void UnregisterAll()
    {
        foreach (int id in _byId.Keys.ToList())
        {
            UnregisterHotKey(Handle, id);
        }
        _byId.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (_byId.TryGetValue(id, out var act))
            {
                try { act(); } catch { /* never let a handler crash the message pump */ }
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        try { UnregisterAll(); } catch { }
        if (Handle != IntPtr.Zero) DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
