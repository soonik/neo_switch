using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NeoSwitch.App;

/// <summary>
/// Toggles the <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>
/// entry that Windows uses to auto-start user-mode apps on logon.
/// No admin rights required.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartupRegistrar
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NeoSwitch";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    /// <summary>
    /// Set or clear the Run value. Returns true on success. On failure the
    /// exception is swallowed; UI should handle the mismatch between intent
    /// and <see cref="IsEnabled"/> on next tick.
    /// </summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                             ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                // Quote in case the install path contains spaces.
                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch { return false; }
    }
}
