using NeoSwitch.Core;

namespace NeoSwitch.App;

public enum RuntimeState
{
    Disconnected,
    Connecting,
    Connected,
    Paused,
    Error,
}

/// <summary>
/// Owns the end-to-end wiring from the ForegroundWatcher → RuleEngine
/// → DebouncedSwitcher → KeyboardClient pipeline and exposes a stable
/// surface of state + events for the UI.
///
/// All events may fire on arbitrary threads — subscribers on the UI
/// thread should marshal via <c>Control.BeginInvoke</c>.
/// </summary>
public sealed class RuntimeController : IDisposable
{
    private readonly Settings _settings;
    private readonly object _lock = new();
    private bool _disposed;

    private KeyboardClient? _kb;
    private ForegroundWatcher? _watcher;
    private RuleEngine? _engine;
    private DebouncedSwitcher? _switcher;

    // ----- observable state -----
    public RuntimeState State { get; private set; } = RuntimeState.Disconnected;
    public string? StateDetail { get; private set; }
    public string? ConnectedProduct { get; private set; }
    public int? ConnectedVid { get; private set; }
    public int? ConnectedPid { get; private set; }
    public byte? CurrentProfileIdx { get; private set; }
    public byte? ProfileCount { get; private set; }
    public IReadOnlyList<ProfileInfo>? Profiles { get; private set; }
    public string? LastForegroundExe { get; private set; }
    public DateTime? LastSentAt { get; private set; }
    public byte? LastSentProfile { get; private set; }

    // ----- events -----
    public event Action? StateChanged;
    public event Action<string>? LogLine;

    public RuntimeController(Settings settings)
    {
        _settings = settings;
    }

    /// <summary>Open the keyboard and install the foreground hook.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (_kb != null) return;
            TransitionTo(RuntimeState.Connecting, "opening keyboard…");
        }

        // Open device outside the lock — can be slow / throw.
        try
        {
            var dev = PickDevice();
            if (dev == null)
            {
                TransitionTo(RuntimeState.Disconnected, "no matching keyboard");
                return;
            }
            var kb = KeyboardClient.Open(dev);
            byte count = kb.GetProfileCount();
            var profiles = kb.LoadAllProfiles();
            byte current = kb.GetCurrentProfileIdx();

            lock (_lock)
            {
                _kb = kb;
                ProfileCount = count;
                Profiles = profiles;
                CurrentProfileIdx = current;
                ConnectedProduct = kb.ProductName;
                ConnectedVid = kb.VendorId;
                ConnectedPid = kb.ProductId;
                InstallPipelineLocked();
            }

            TransitionTo(
                _settings.Paused ? RuntimeState.Paused : RuntimeState.Connected,
                _settings.Paused ? "paused" : "connected");
        }
        catch (Exception ex)
        {
            TransitionTo(RuntimeState.Error, ex.Message);
            Log($"open failed: {ex.Message}");
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _watcher?.Dispose();
            _switcher?.Dispose();
            _kb?.Dispose();
            _watcher = null;
            _switcher = null;
            _engine = null;
            _kb = null;
            CurrentProfileIdx = null;
            ProfileCount = null;
            Profiles = null;
            ConnectedProduct = null;
            ConnectedVid = null;
            ConnectedPid = null;
        }
        TransitionTo(RuntimeState.Disconnected, "stopped");
    }

    public void Reconnect()
    {
        Stop();
        Start();
    }

    public void TogglePause()
    {
        _settings.Paused = !_settings.Paused;
        TransitionTo(
            _settings.Paused ? RuntimeState.Paused :
            (_kb != null ? RuntimeState.Connected : RuntimeState.Disconnected),
            _settings.Paused ? "paused" : "running");
    }

    /// <summary>
    /// Refresh rule engine from <see cref="Settings"/>. Call after the user
    /// edits watched apps or fg/bg profile indices.
    /// </summary>
    public void ApplySettings()
    {
        lock (_lock)
        {
            if (_engine == null) return;
            _engine.ForegroundProfile = _settings.ForegroundProfile;
            _engine.BackgroundProfile = _settings.BackgroundProfile;
            _engine.WatchedExes.Clear();
            foreach (var app in _settings.WatchedApps) _engine.WatchedExes.Add(app);
            _engine.Reset();  // force next event to emit as a change
        }
    }

    /// <summary>Immediately switch to <paramref name="idx"/>, bypassing rules.</summary>
    public void ForceProfile(byte idx)
    {
        KeyboardClient? kb;
        lock (_lock) { kb = _kb; }
        if (kb == null) throw new InvalidOperationException("not connected");
        kb.SwitchProfile(idx);
        lock (_lock) { CurrentProfileIdx = idx; }
        RaiseStateChanged();
        Log($"force-switch -> profile {idx}");
    }

    public void Dispose()
    {
        lock (_lock) { _disposed = true; }
        Stop();
    }

    // ----- helpers -----

    private HidSharp.HidDevice? PickDevice()
    {
        var all = KeyboardClient.FindAll(_settings.VendorId, _settings.ProductId);
        return all.Count == 0 ? null : all[0];
    }

    private void InstallPipelineLocked()
    {
        var engine = new RuleEngine
        {
            ForegroundProfile = _settings.ForegroundProfile,
            BackgroundProfile = _settings.BackgroundProfile,
        };
        foreach (var app in _settings.WatchedApps) engine.WatchedExes.Add(app);

        var switcher = new DebouncedSwitcher(_kb!, _settings.SwitchDelayMs)
        {
            Gate = () => !NeoSwitch.Core.ModifierKeys.AnyHeld(),
            GateTimeoutMs = _settings.GateTimeoutMs,
        };

        switcher.Sent += target =>
        {
            lock (_lock)
            {
                CurrentProfileIdx = target;
                LastSentProfile = target;
                LastSentAt = DateTime.Now;
            }
            Log($"SENT profile {target}");
            RaiseStateChanged();
        };
        switcher.Failed += (target, ex) =>
        {
            Log($"SEND FAIL -> {target}: {ex.Message}");
            TransitionTo(RuntimeState.Error, ex.Message);
        };

        engine.Decided += dec =>
        {
            lock (_lock) { LastForegroundExe = dec.App.Executable; }
            RaiseStateChanged();
            if (!dec.ProfileChanged) return;
            if (_settings.Paused) { Log($"(paused)  fg={dec.App.Executable} -> would be {dec.TargetProfile}"); return; }
            switcher.Schedule(dec.TargetProfile);
        };

        var watcher = new ForegroundWatcher();
        watcher.Changed += engine.OnForegroundChanged;
        watcher.Start();

        _engine = engine;
        _switcher = switcher;
        _watcher = watcher;
    }

    private void TransitionTo(RuntimeState s, string? detail)
    {
        lock (_lock) { State = s; StateDetail = detail; }
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); } catch { }
    }

    private void Log(string line)
    {
        try { LogLine?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {line}"); } catch { }
    }
}
