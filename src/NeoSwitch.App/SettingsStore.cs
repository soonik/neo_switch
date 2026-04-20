using System.Text.Json;

namespace NeoSwitch.App;

/// <summary>
/// JSON-backed settings container. Writes to
/// <c>%APPDATA%\NeoSwitch\config.json</c> by default.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public string ConfigPath { get; }
    public Settings Current { get; private set; }

    public SettingsStore() : this(DefaultPath()) { }

    public SettingsStore(string path)
    {
        ConfigPath = path;
        Current = Load();
    }

    public static string DefaultPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NeoSwitch");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }

    private Settings Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new Settings();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }
        catch
        {
            // Corrupt file → start fresh. Keep the broken one for debugging.
            try { File.Move(ConfigPath, ConfigPath + ".broken", overwrite: true); } catch { }
            return new Settings();
        }
    }

    public void Save()
    {
        string json = JsonSerializer.Serialize(Current, JsonOpts);
        string tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPath, overwrite: true);
    }
}
