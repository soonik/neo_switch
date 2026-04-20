using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NeoSwitch.Core;

/// <summary>
/// Information about the process that owns the foreground window.
/// </summary>
public readonly record struct ForegroundApp(
    int ProcessId,
    string Executable,   // lowercase basename, e.g. "valorant.exe"
    string FullPath,     // full image path
    IntPtr Hwnd);

/// <summary>
/// A foreground-window transition that <see cref="ForegroundWatcher"/> filtered
/// out (shell tray, alt-tab overlay, cloaked UWP, invisible helper windows…).
/// </summary>
public readonly record struct SkippedEvent(
    IntPtr Hwnd,
    string WindowClass,
    string Executable,
    int ProcessId,
    string Reason);

/// <summary>
/// Event-driven foreground-window watcher backed by
/// <c>SetWinEventHook(EVENT_SYSTEM_FOREGROUND)</c>. Windows-only.
///
/// Runs a dedicated STA thread with a GetMessage/DispatchMessage pump so
/// WINEVENT_OUTOFCONTEXT callbacks are delivered. Call <see cref="Start"/>
/// once; subscribers to <see cref="Changed"/> get one event per foreground
/// transition, plus one seed event at startup for the current foreground.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ForegroundWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WM_QUIT = 0x0012;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public event Action<ForegroundApp>? Changed;

    /// <summary>
    /// Raised for foreground events that were filtered out (transient shell
    /// windows, cloaked UWP surfaces, etc.). Subscribe for diagnostics.
    /// </summary>
    public event Action<SkippedEvent>? Skipped;

    /// <summary>
    /// Window class names that are never treated as user-foreground. Taskbar,
    /// tray, alt-tab overlay, Start / quick-settings flyouts, and friends.
    /// Add your own entries if you see spurious switches in <c>--log-filtered</c>.
    /// </summary>
    public HashSet<string> IgnoredWindowClasses { get; } = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TaskSwitcherWnd",
        "TaskSwitcherOverlayWnd",
        "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "Shell_InputSwitchTopLevelWindow",
        "DV2ControlHost",
        "Windows.Internal.Shell.TabProxyWindow",
        "Windows.UI.Core.CoreComponentInputSource",
    };

    private Thread? _thread;
    private uint _threadId;
    private volatile bool _running;
    private IntPtr _hook;
    private WinEventDelegate? _cb;   // keep delegate rooted for GC

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime);

    public void Start()
    {
        if (_thread != null) throw new InvalidOperationException("ForegroundWatcher already started.");
        var ready = new ManualResetEventSlim();
        Exception? startError = null;

        _running = true;
        _thread = new Thread(() =>
        {
            try { Pump(ready); }
            catch (Exception ex) { startError = ex; ready.Set(); }
        })
        {
            IsBackground = true,
            Name = "NeoSwitch-FgWatcher",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();

        if (startError != null) throw new InvalidOperationException(
            "failed to install foreground hook.", startError);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException(
                "SetWinEventHook returned NULL (error " + Marshal.GetLastWin32Error() + ").");
    }

    public void Stop()
    {
        if (_thread == null) return;
        _running = false;
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Pump(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        _cb = OnWinEvent;
        // NOT using WINEVENT_SKIPOWNPROCESS: the host application's own windows
        // (e.g. the NeoSwitch main form) are legitimate "not-watched" foregrounds
        // that should flip the keyboard to the background profile.
        _hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _cb, 0, 0,
            WINEVENT_OUTOFCONTEXT);

        // Seed subscribers with the current foreground window before returning.
        try { EmitFor(GetForegroundWindow()); } catch { /* swallow */ }

        ready.Set();
        if (_hook == IntPtr.Zero) return;

        try
        {
            while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            _cb = null;
        }
    }

    private void OnWinEvent(IntPtr hook, uint type, IntPtr hwnd,
                            int idObject, int idChild, uint thread, uint time)
    {
        if (type != EVENT_SYSTEM_FOREGROUND) return;
        if (hwnd == IntPtr.Zero) return;
        // Fire only for top-level window objects (idObject == OBJID_WINDOW == 0).
        if (idObject != 0) return;
        EmitFor(hwnd);
    }

    private void EmitFor(IntPtr hwnd)
    {
        if (!IsUserForeground(hwnd, out string? skipReason, out string className))
        {
            var skip = Skipped;
            if (skip != null)
            {
                TryGetExe(hwnd, out int spid, out string spath);
                string sexe = string.IsNullOrEmpty(spath) ? "(unknown)" : Path.GetFileName(spath).ToLowerInvariant();
                try { skip(new SkippedEvent(hwnd, className, sexe, spid, skipReason ?? "")); }
                catch { /* swallow */ }
            }
            return;
        }

        if (!TryGetExe(hwnd, out int pid, out string fullPath)) return;
        string exe = Path.GetFileName(fullPath).ToLowerInvariant();
        var handler = Changed;
        if (handler == null) return;
        try { handler(new ForegroundApp(pid, exe, fullPath, hwnd)); }
        catch { /* never let a subscriber kill the pump */ }
    }

    private bool IsUserForeground(IntPtr hwnd, out string? reason, out string className)
    {
        className = "";
        if (hwnd == IntPtr.Zero) { reason = "null hwnd"; return false; }

        if (!IsWindowVisible(hwnd)) { reason = "not visible"; return false; }

        // DWM cloaking: suspended UWP tabs, offscreen shell surfaces.
        int cloaked = 0;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, ref cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            reason = "cloaked";
            return false;
        }

        var sb = new StringBuilder(128);
        int n = GetClassName(hwnd, sb, sb.Capacity);
        className = n > 0 ? sb.ToString(0, n) : "";

        if (className.Length == 0) { reason = "empty class"; return false; }

        if (IgnoredWindowClasses.Contains(className))
        {
            reason = $"shell class '{className}'";
            return false;
        }

        reason = null;
        return true;
    }

    private static bool TryGetExe(IntPtr hwnd, out int pid, out string fullPath)
    {
        pid = 0;
        fullPath = "";
        GetWindowThreadProcessId(hwnd, out uint p);
        if (p == 0) return false;
        pid = (int)p;

        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var buf = new StringBuilder(1024);
            int size = buf.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buf, ref size)) return false;
            fullPath = buf.ToString(0, size);
            return true;
        }
        finally { CloseHandle(handle); }
    }

    // -------- P/Invoke --------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }
}
