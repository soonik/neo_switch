namespace NeoSwitch.Core;

/// <summary>
/// Result of a single foreground-app evaluation.
/// </summary>
public readonly record struct RuleDecision(
    ForegroundApp App,
    byte TargetProfile,
    bool ProfileChanged);

/// <summary>
/// Pure decision logic for the foreground/background profile split.
/// Feed it <see cref="ForegroundApp"/> events, subscribe to <see cref="Decided"/>,
/// and the engine tells you the target profile plus whether it differs from
/// what it last decided — so callers can skip redundant HID writes.
/// </summary>
public sealed class RuleEngine
{
    /// <summary>Case-insensitive basenames, e.g. "valorant.exe".</summary>
    public HashSet<string> WatchedExes { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    public byte ForegroundProfile { get; set; }
    public byte BackgroundProfile { get; set; }

    /// <summary>The last <see cref="TargetProfile"/> emitted, or null before the first call.</summary>
    public byte? LastTarget { get; private set; }

    public event Action<RuleDecision>? Decided;

    public void OnForegroundChanged(ForegroundApp app)
    {
        byte target = WatchedExes.Contains(app.Executable)
            ? ForegroundProfile
            : BackgroundProfile;

        bool changed = LastTarget != target;
        LastTarget = target;

        Decided?.Invoke(new RuleDecision(app, target, changed));
    }

    /// <summary>Forget the last target so the next event is emitted as a change regardless.</summary>
    public void Reset() => LastTarget = null;
}
