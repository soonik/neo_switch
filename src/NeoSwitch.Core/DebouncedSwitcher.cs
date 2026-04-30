namespace NeoSwitch.Core;

/// <summary>
/// Coalesces rapid <see cref="KeyboardClient.SwitchProfile"/> calls into a single
/// HID write, deferred until no modifier key is physically held.
///
/// Why this exists: when the QwertyKeys firmware processes <c>D0 B1 &lt;idx&gt;</c>
/// the matrix scan is briefly suspended/reset, and any key held at that instant
/// can lose its key-up event. For normal alphanumerics this self-corrects on the
/// next press. For modifiers (Alt, Ctrl, Shift, Win) the OS stays stuck in a
/// "phantom held" state until the key is pressed again — the classic stuck-Alt
/// after Alt+Tab.
///
/// Mitigation layers (in order of application):
///   1. Debounce: wait <see cref="DelayMs"/> of quiet after the last <see cref="Schedule"/>.
///      This also coalesces rapid alt-tab chains into one write.
///   2. Modifier gate (<see cref="Gate"/>): before the write, poll the gate at
///      20 ms intervals up to <see cref="GateTimeoutMs"/>; only write when it
///      returns true. A CLI/UI wires this to "no modifier held".
/// </summary>
public sealed class DebouncedSwitcher : IDisposable
{
    private readonly KeyboardClient _kb;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private byte? _pendingTarget;
    private bool _disposed;

    /// <summary>Quiet period (ms) the scheduler must observe before firing the write.</summary>
    public int DelayMs { get; }

    /// <summary>
    /// Gate predicate: returns <c>true</c> when it is safe to issue the HID
    /// write. Typically "no modifier keys held". Set to <c>null</c> to disable.
    /// </summary>
    public Func<bool>? Gate { get; set; }

    /// <summary>How long to poll <see cref="Gate"/> before giving up and writing anyway. Default 1000 ms.</summary>
    public int GateTimeoutMs { get; set; } = 1000;

    /// <summary>Gate polling interval in ms. Default 20 ms.</summary>
    public int GatePollMs { get; set; } = 20;

    /// <summary>
    /// Optional snapshot taken right before the HID write fires. The result
    /// (if non-null) is forwarded to <see cref="PostWrite"/> after a successful
    /// send, so callers can recover from key-state damage caused by the
    /// firmware's matrix reset.
    /// </summary>
    public Func<object?>? CaptureBeforeWrite { get; set; }

    /// <summary>
    /// Optional post-write callback. Receives the value returned by
    /// <see cref="CaptureBeforeWrite"/>. Invoked on the timer thread, after
    /// the HID write succeeds and after <see cref="Sent"/> fires. Callers
    /// MAY perform their own delays inside; the switcher does no extra
    /// waiting.
    /// </summary>
    public Action<object?>? PostWrite { get; set; }

    /// <summary>Raised on the timer thread after the HID write succeeds.</summary>
    public event Action<byte>? Sent;

    /// <summary>Raised when the write is deferred because <see cref="Gate"/> returned false.</summary>
    public event Action<int>? Waiting;

    /// <summary>Raised on the timer thread when the HID write throws.</summary>
    public event Action<byte, Exception>? Failed;

    public DebouncedSwitcher(KeyboardClient kb, int delayMs = 200)
    {
        _kb = kb ?? throw new ArgumentNullException(nameof(kb));
        DelayMs = Math.Max(0, delayMs);
        _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Request a profile switch. If another <see cref="Schedule"/> arrives
    /// within <see cref="DelayMs"/> the new target replaces the pending one.
    /// </summary>
    public void Schedule(byte target)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _pendingTarget = target;
            _timer.Change(DelayMs, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _pendingTarget = null;
        }
        _timer.Dispose();
    }

    private void OnTimer(object? _)
    {
        // 1) Wait for the gate (e.g. "no modifier held") up to GateTimeoutMs.
        //    We poll rather than subscribing to key events — simple & good enough.
        int waited = 0;
        bool notified = false;
        var gate = Gate;
        while (gate != null && !_disposed)
        {
            bool ok;
            try { ok = gate(); }
            catch { ok = true; }  // never let a bad gate block forever
            if (ok) break;
            if (waited >= GateTimeoutMs) break;
            if (!notified) { notified = true; Waiting?.Invoke(waited); }
            Thread.Sleep(GatePollMs);
            waited += GatePollMs;
        }

        // 2) Dequeue the latest target AFTER the wait so that any Schedule()
        //    arriving during the wait overwrites and we write the freshest target.
        byte target;
        lock (_lock)
        {
            if (_disposed || _pendingTarget is not byte t) return;
            target = t;
            _pendingTarget = null;
        }

        // Capture pre-write state (e.g. held keys) so PostWrite can act on it.
        object? snapshot = null;
        try { snapshot = CaptureBeforeWrite?.Invoke(); } catch { }

        try
        {
            _kb.SwitchProfile(target);
            Sent?.Invoke(target);
            try { PostWrite?.Invoke(snapshot); } catch { }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(target, ex);
        }
    }
}
