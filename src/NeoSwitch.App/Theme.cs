using System.Drawing;

namespace NeoSwitch.App;

/// <summary>
/// Dark palette + fonts derived from <c>mockup/neo-switch.html</c>.
/// Kept as plain static fields so any control can grab <see cref="Background"/>
/// etc. without allocating extra state. Tweak here, not inline.
/// </summary>
public static class Theme
{
    // Surfaces
    public static readonly Color Background = Color.FromArgb(0x20, 0x20, 0x20);
    public static readonly Color Panel       = Color.FromArgb(0x2B, 0x2B, 0x2B);
    public static readonly Color PanelAlt    = Color.FromArgb(0x33, 0x33, 0x33);
    public static readonly Color InputBg     = Color.FromArgb(0x26, 0x26, 0x26);
    public static readonly Color Chrome      = Color.FromArgb(0x1A, 0x1A, 0x1A);
    public static readonly Color Border      = Color.FromArgb(0x3F, 0x3F, 0x3F);
    public static readonly Color ActiveRow   = Color.FromArgb(0x1B, 0x33, 0x44);

    // Text
    public static readonly Color Text  = Color.FromArgb(0xE6, 0xE6, 0xE6);
    public static readonly Color Muted = Color.FromArgb(0x9A, 0x9A, 0x9A);

    // Accents
    public static readonly Color Accent       = Color.FromArgb(0x4C, 0xC2, 0xFF);
    public static readonly Color AccentDark   = Color.FromArgb(0x3A, 0xA8, 0xE0);
    public static readonly Color AccentTrack  = Color.FromArgb(0x15, 0x30, 0x47);
    public static readonly Color AccentBorder = Color.FromArgb(0x1E, 0x47, 0x63);

    // Status dot / chip
    public static readonly Color Good  = Color.FromArgb(0x6C, 0xCF, 0x76);
    public static readonly Color Warn  = Color.FromArgb(0xFF, 0xC3, 0x00);
    public static readonly Color Error = Color.FromArgb(0xE6, 0x66, 0x66);
    public static readonly Color Idle  = Color.FromArgb(0x77, 0x77, 0x77);

    // Fonts
    public static readonly Font Body      = new("Segoe UI", 9f);
    public static readonly Font Heading   = new("Segoe UI Semibold", 10f);
    public static readonly Font SectionLbl = new("Segoe UI Semibold", 8.5f);
    public static readonly Font Code      = new("Consolas", 8.5f);
    public static readonly Font Chip      = new("Segoe UI Semibold", 8.5f);
}
