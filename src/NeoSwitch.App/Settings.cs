namespace NeoSwitch.App;

/// <summary>
/// Persisted user configuration. Serialised to JSON at
/// <see cref="SettingsStore.ConfigPath"/>.
/// </summary>
public sealed class Settings
{
    /// <summary>Schema version for future migrations. Bump when fields change incompatibly.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Lowercase .exe basenames, e.g. "valorant.exe".</summary>
    public List<string> WatchedApps { get; set; } = new();

    /// <summary>Profile index sent when a watched app is foreground.</summary>
    public byte ForegroundProfile { get; set; } = 1;

    /// <summary>Profile index sent when no watched app is foreground.</summary>
    public byte BackgroundProfile { get; set; } = 0;

    /// <summary>Preferred USB vendor ID. Null = auto-pick first raw-HID match.</summary>
    public int? VendorId { get; set; }

    /// <summary>Preferred USB product ID. Null = auto-pick.</summary>
    public int? ProductId { get; set; }

    /// <summary>Quiet-period debounce before the HID write (ms).</summary>
    public int SwitchDelayMs { get; set; } = 200;

    /// <summary>How long to wait for modifier keys to release before writing (ms).</summary>
    public int GateTimeoutMs { get; set; } = 1000;

    /// <summary>If true, the runtime observes foreground changes but issues no HID writes.</summary>
    public bool Paused { get; set; }

    /// <summary>Mirrors the HKCU\...\Run registry key; synced by StartupRegistrar on save.</summary>
    public bool StartWithWindows { get; set; }
}
