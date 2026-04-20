namespace NeoSwitch.Core;

/// <summary>
/// Coalesces rapid <see cref="KeyboardClient.SwitchProfile"/> calls into a single
/// HID write per quiet period.
///
/// Why this exists: when the QwertyKeys firmware processes <c>D0 B1 &lt;idx&gt;</c>
/// the matrix scan is briefly suspended/reset, and any key held at that exact
/// instant can lose its key-up event — leaving Windows convinced that key is
/// still down (a stuck Alt or Tab after Alt+Tab is the classic case).
///
/// By waiting <see cref="DelayMs"/> ms after the most recent <see cref="Schedule"/>
/// call before issuing the actual HID write, we give the user time to release
/// modifier keys before the firmware resets, and we collapse rapid alt-tab
/// chains into one write.
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

    /// <summary>Raised on the timer thread after the HID write succeeds.</summary>
    public event Action<byte>? Sent;

    /// <summary>Raised on the timer thread when the HID write throws.</summary>
    public event Action<byte, Exception>? Failed;

    public DebouncedSwitcher(KeyboardClient kb, int delayMs = 200)
    {
        _kb = kb ?? throw new ArgumentNullException(nameof(kb));
        DelayMs = Math.Max(0, delayMs);
        _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Request a profile switch. If another <see cref="Schedule"/> arrives within
    /// <see cref="DelayMs"/> the new target replaces the pending one.
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
        byte target;
        lock (_lock)
        {
            if (_disposed || _pendingTarget is not byte t) return;
            target = t;
            _pendingTarget = null;
        }
        try
        {
            _kb.SwitchProfile(target);
            Sent?.Invoke(target);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(target, ex);
        }
    }
}
