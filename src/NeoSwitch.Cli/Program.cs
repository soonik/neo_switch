using HidSharp;
using NeoSwitch.Core;

namespace NeoSwitch.Cli;

internal static class Program
{
    static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    static int Run(string[] args)
    {
        var opts = ParseOptions(ref args);

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        string cmd = args[0].ToLowerInvariant();
        string[] rest = args.Skip(1).ToArray();

        return cmd switch
        {
            "list"   => CmdList(opts, rest),
            "info"   => CmdInfo(opts),
            "get"    => CmdGet(opts),
            "switch" => CmdSwitch(opts, rest),
            "probe"  => CmdProbe(opts),
            "raw"    => CmdRaw(opts, rest),
            "watch"  => CmdWatch(opts, rest),
            _        => Fail($"unknown command '{cmd}' — run with --help for usage.")
        };
    }

    // ---------------- commands ----------------

    static int CmdList(Options opts, string[] rest)
    {
        bool all = rest.Contains("--all") || rest.Contains("-a");
        if (all)
        {
            PrintAllHidDevices(opts);
            return 0;
        }

        var devs = KeyboardClient.FindAll(opts.VendorId, opts.ProductId);
        if (devs.Count == 0)
        {
            Console.WriteLine("no QwertyKeys (raw-HID, usage FF60:0061) devices found.");
            Console.WriteLine("run with '--all' to see every HID device on the system.");
            return 0;
        }
        Console.WriteLine($"{devs.Count} matching device(s)   (use --device <n> to pick)");
        for (int i = 0; i < devs.Count; i++)
            PrintDevice(devs[i], indent: "  ", index: i);
        return 0;
    }

    static void PrintAllHidDevices(Options opts)
    {
        var all = KeyboardClient.AllHidDevices();
        int shown = 0;
        foreach (var d in all)
        {
            if (opts.VendorId is int v && d.VendorID != v) continue;
            if (opts.ProductId is int p && d.ProductID != p) continue;
            PrintDevice(d, indent: "  ");
            shown++;
        }
        Console.WriteLine();
        Console.WriteLine($"{shown} HID device(s) total{(opts.VendorId.HasValue || opts.ProductId.HasValue ? " (after vid/pid filter)" : "")}.");
    }

    static void PrintDevice(HidDevice d, string indent, int? index = null)
    {
        string name = Try(() => d.GetProductName()) ?? "(no name)";
        string mfg  = Try(() => d.GetManufacturer()) ?? "(no mfg)";
        var usages  = KeyboardClient.GetUsages(d);
        bool raw    = usages.Contains(Protocol.RawHidUsage);
        string prefix = index is int i ? $"[{i}] " : "";
        Console.WriteLine($"{indent}{prefix}vid=0x{d.VendorID:X4} pid=0x{d.ProductID:X4}  {mfg} / {name}   " +
                          $"{(raw ? "[raw-HID *]" : "")}");
        Console.WriteLine($"{indent}  path: {d.DevicePath}");
        int outLen = SafeLen(() => d.GetMaxOutputReportLength());
        int inLen  = SafeLen(() => d.GetMaxInputReportLength());
        Console.WriteLine($"{indent}  report sizes: out={outLen} in={inLen}");
        if (usages.Count > 0)
        {
            Console.Write($"{indent}  usages: ");
            Console.WriteLine(string.Join(", ",
                usages.Select(u => $"{(ushort)(u >> 16):X4}:{(ushort)u:X4}")));
        }
    }

    static int CmdInfo(Options opts)
    {
        using var kb = MustOpen(opts);
        Console.WriteLine($"Connected: {kb.Manufacturer} / {kb.ProductName}");
        Console.WriteLine($"  vid=0x{kb.VendorId:X4} pid=0x{kb.ProductId:X4}");
        Console.WriteLine($"  path: {kb.DevicePath}");
        Console.WriteLine($"  report sizes: out={kb.MaxOutputReportLength} in={kb.MaxInputReportLength}");

        byte current = kb.GetCurrentProfileIdx();
        byte count   = kb.GetProfileCount();
        Console.WriteLine($"  profiles: {count}   current: {current}");

        var list = kb.LoadAllProfiles();
        foreach (var p in list)
        {
            string marker = p.Index == current ? "*" : " ";
            string name = string.IsNullOrWhiteSpace(p.Name) ? "(unnamed)" : p.Name;
            Console.WriteLine($"  {marker} [{p.Index}] {name,-28} {p.Color}");
        }
        return 0;
    }

    static int CmdGet(Options opts)
    {
        using var kb = MustOpen(opts);
        Console.WriteLine(kb.GetCurrentProfileIdx());
        return 0;
    }

    static int CmdSwitch(Options opts, string[] rest)
    {
        if (rest.Length != 1 || !TryParseByte(rest[0], out byte idx))
            return Fail("usage: neoswitch switch <index>");

        using var kb = MustOpen(opts);
        byte count = kb.GetProfileCount();
        if (idx >= count)
            return Fail($"index {idx} out of range (device has {count} profiles).");

        kb.SwitchProfile(idx);
        byte now = kb.GetCurrentProfileIdx();
        Console.WriteLine($"switched to profile {now}");
        return now == idx ? 0 : 2;
    }

    static int CmdProbe(Options opts)
    {
        using var kb = MustOpen(opts, forceVerbose: true);
        Console.WriteLine($"device: {kb.Manufacturer} / {kb.ProductName}  vid=0x{kb.VendorId:X4} pid=0x{kb.ProductId:X4}");
        Console.WriteLine($"path:   {kb.DevicePath}");
        Console.WriteLine($"sizes:  out={kb.MaxOutputReportLength} in={kb.MaxInputReportLength}");
        Console.WriteLine();

        // 1. VIA standard: protocol version. Every VIA-compatible firmware replies.
        Console.WriteLine("[1/2] VIA protocol-version query (0x01) — should work on any VIA-enabled firmware:");
        try
        {
            var reply = kb.SendRaw(stackalloc byte[] { 0x01 }, matchLen: 1);
            int version = (reply.Length >= 3) ? (reply[1] << 8) | reply[2] : -1;
            Console.WriteLine($"    reply (hex): {KeyboardClient.Hex(reply)}");
            if (version >= 0) Console.WriteLine($"    protocol version: 0x{version:X4}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    FAILED: {ex.Message}");
            Console.WriteLine("    → transport / interface is wrong. `list --all` to inspect HID devices.");
            return 2;
        }

        // 2. QwertyKeys custom: profile-index query under 0xD0.
        Console.WriteLine();
        Console.WriteLine("[2/2] QwertyKeys actuation cmd (D0 B0) — current profile index:");
        try
        {
            var reply = kb.SendRaw(stackalloc byte[] { Protocol.CmdCustomActuation, Protocol.Profile.GetIdx });
            Console.WriteLine($"    reply (hex): {KeyboardClient.Hex(reply)}");
            if (reply.Length >= 3) Console.WriteLine($"    current profile idx: {reply[2]}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    FAILED: {ex.Message}");
            Console.WriteLine("    → VIA worked but the 0xD0 custom namespace did not. Possible causes:");
            Console.WriteLine("      - older firmware that predates the actuation extension");
            Console.WriteLine("      - a different keyboard model from the one he.qwertykeys.com targets");
            Console.WriteLine("      - another USB interface of the same keyboard holds the raw-HID we want");
            return 3;
        }

        Console.WriteLine();
        Console.WriteLine("all checks passed — `neoswitch info` / `switch <n>` should work.");
        return 0;
    }

    static int CmdWatch(Options opts, string[] rest)
    {
        if (!OperatingSystem.IsWindows())
            return Fail("'watch' is Windows-only.");

        WatchArgs cfg;
        try { cfg = ParseWatchArgs(rest); }
        catch (FormatException ex) { return Fail(ex.Message); }

        if (cfg.Apps.Count == 0)
            return Fail("watch: specify at least one --app <exe.exe> (e.g. --app valorant.exe).");

        using var kb = MustOpen(opts);
        byte count = kb.GetProfileCount();
        if (cfg.FgProfile >= count || cfg.BgProfile >= count)
            return Fail($"profile index out of range; device reports {count} profiles.");

        byte current = kb.GetCurrentProfileIdx();
        Console.Error.WriteLine(
            $"connected: {kb.Manufacturer} / {kb.ProductName}  (profiles={count}, current={current})");

        var engine = new RuleEngine
        {
            ForegroundProfile = cfg.FgProfile,
            BackgroundProfile = cfg.BgProfile,
        };
        foreach (var app in cfg.Apps) engine.WatchedExes.Add(app);

        using var watcher = new ForegroundWatcher();
        using var switcher = new DebouncedSwitcher(kb, cfg.SwitchDelayMs);

        if (cfg.LogFiltered)
        {
            watcher.Skipped += s =>
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss.fff}]   SKIP      class='{s.WindowClass}' exe={s.Executable}  ({s.Reason})");
        }

        switcher.Sent += target =>
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]   SENT profile {target}");
        switcher.Failed += (target, ex) =>
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]   SEND FAILED -> {target}: {ex.Message}");

        engine.Decided += dec =>
        {
            string tag = dec.ProfileChanged ? "SWITCH" : "      ";
            bool watched = engine.WatchedExes.Contains(dec.App.Executable);
            string marker = watched ? "*" : " ";
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss.fff}] {tag} {marker} fg={dec.App.Executable,-32} -> profile {dec.TargetProfile}");

            if (!dec.ProfileChanged || cfg.DryRun) return;
            switcher.Schedule(dec.TargetProfile);
        };
        watcher.Changed += engine.OnForegroundChanged;

        watcher.Start();

        Console.WriteLine($"watching {cfg.Apps.Count} app(s): {string.Join(", ", cfg.Apps)}");
        Console.WriteLine(
            $"fg profile = {cfg.FgProfile}   bg profile = {cfg.BgProfile}   " +
            $"switch delay = {cfg.SwitchDelayMs}ms{(cfg.DryRun ? "   [DRY RUN — no HID writes]" : "")}");
        Console.WriteLine("(Ctrl+C to stop)");
        Console.WriteLine();

        var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        done.Wait();

        Console.WriteLine("stopping...");
        watcher.Stop();
        return 0;
    }

    sealed record WatchArgs(
        List<string> Apps, byte FgProfile, byte BgProfile,
        bool DryRun, int SwitchDelayMs, bool LogFiltered);

    static WatchArgs ParseWatchArgs(string[] args)
    {
        var apps = new List<string>();
        byte fg = 1, bg = 0;
        bool dry = false;
        int delayMs = 200;
        bool logFiltered = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--app" when i + 1 < args.Length:
                    apps.Add(args[++i].ToLowerInvariant());
                    break;
                case "--fg":
                case "--fg-profile":
                    if (i + 1 >= args.Length || !TryParseByte(args[++i], out fg))
                        throw new FormatException($"bad {a} value");
                    break;
                case "--bg":
                case "--bg-profile":
                    if (i + 1 >= args.Length || !TryParseByte(args[++i], out bg))
                        throw new FormatException($"bad {a} value");
                    break;
                case "--switch-delay":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out delayMs) || delayMs < 0)
                        throw new FormatException($"bad {a} value (expected non-negative integer ms)");
                    break;
                case "--dry-run":
                    dry = true;
                    break;
                case "--log-filtered":
                    logFiltered = true;
                    break;
                default:
                    throw new FormatException($"watch: unknown option '{a}'");
            }
        }
        return new WatchArgs(apps, fg, bg, dry, delayMs, logFiltered);
    }

    static int CmdRaw(Options opts, string[] rest)
    {
        if (rest.Length == 0)
            return Fail("usage: neoswitch raw <hex bytes>   (e.g. 'neoswitch raw D0 B0'  or  '0xD0 0xB0')");

        byte[] bytes;
        try
        {
            bytes = KeyboardClient.ParseHex(string.Join(' ', rest));
        }
        catch (FormatException ex)
        {
            return Fail(ex.Message);
        }
        if (bytes.Length == 0) return Fail("no bytes to send.");

        using var kb = MustOpen(opts, forceVerbose: true);
        Console.WriteLine($"sending: {KeyboardClient.Hex(bytes)}");
        var reply = kb.SendRaw(bytes, matchLen: Math.Min(2, bytes.Length));
        Console.WriteLine($"reply:   {KeyboardClient.Hex(reply)}");
        return 0;
    }

    // ---------------- helpers ----------------

    static KeyboardClient MustOpen(Options opts, bool forceVerbose = false)
    {
        var devs = KeyboardClient.FindAll(opts.VendorId, opts.ProductId);
        if (devs.Count == 0)
            throw new InvalidOperationException(
                "no matching keyboard found. Try 'neoswitch list' — or 'neoswitch list --all' to see every HID device.");

        HidDevice chosen;
        if (opts.DeviceIndex is int di)
        {
            if (di < 0 || di >= devs.Count)
                throw new InvalidOperationException(
                    $"--device {di} is out of range ({devs.Count} device(s) match).");
            chosen = devs[di];
        }
        else if (devs.Count == 1)
        {
            chosen = devs[0];
        }
        else
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{devs.Count} raw-HID devices match — pick one explicitly:");
            for (int i = 0; i < devs.Count; i++)
            {
                var d = devs[i];
                string name = Try(() => d.GetProductName()) ?? "(no name)";
                string mfg  = Try(() => d.GetManufacturer()) ?? "(no mfg)";
                sb.AppendLine($"  [{i}] vid=0x{d.VendorID:X4} pid=0x{d.ProductID:X4}  {mfg} / {name}");
            }
            sb.AppendLine();
            sb.Append("  use  --device <index>  (e.g. --device 0)  or  --vid 0xNNNN --pid 0xNNNN");
            throw new InvalidOperationException(sb.ToString());
        }

        var kb = KeyboardClient.Open(chosen);
        kb.ReadTimeoutMs = opts.TimeoutMs;
        if (opts.Verbose || forceVerbose)
            kb.Logger = line => Console.Error.WriteLine($"    [hid] {line}");
        return kb;
    }

    static int Fail(string msg)
    {
        Console.Error.WriteLine($"error: {msg}");
        return 1;
    }

    static bool TryParseByte(string s, out byte value)
    {
        s = s.Trim();
        int radix = 10;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { s = s[2..]; radix = 16; }
        try
        {
            int n = Convert.ToInt32(s, radix);
            if (n >= 0 && n <= 255) { value = (byte)n; return true; }
        }
        catch { }
        value = 0;
        return false;
    }

    static string? Try(Func<string?> fn) { try { return fn(); } catch { return null; } }
    static int SafeLen(Func<int> fn) { try { return fn(); } catch { return -1; } }

    // ---------------- options ----------------

    sealed record Options(int? VendorId, int? ProductId, int? DeviceIndex, bool Verbose, int TimeoutMs);

    static Options ParseOptions(ref string[] args)
    {
        int? vid = null, pid = null, deviceIdx = null;
        bool verbose = false;
        int timeout = 1500;
        var remaining = new List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--vid" when i + 1 < args.Length:
                    if (!TryParseHex(args[++i], out int v)) throw new FormatException($"invalid --vid value '{args[i]}'");
                    vid = v; break;
                case "--pid" when i + 1 < args.Length:
                    if (!TryParseHex(args[++i], out int p)) throw new FormatException($"invalid --pid value '{args[i]}'");
                    pid = p; break;
                case "--device" when i + 1 < args.Length:
                case "-d" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int di)) throw new FormatException($"invalid --device value '{args[i]}'");
                    deviceIdx = di; break;
                case "--timeout" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int t)) throw new FormatException($"invalid --timeout value '{args[i]}'");
                    timeout = t; break;
                case "-v":
                case "--verbose":
                    verbose = true; break;
                default:
                    remaining.Add(args[i]); break;
            }
        }
        args = remaining.ToArray();
        return new Options(vid, pid, deviceIdx, verbose, timeout);
    }

    static bool TryParseHex(string s, out int value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return int.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    static void PrintUsage()
    {
        Console.WriteLine("""
            neoswitch — QwertyKeys profile CLI (M1 prototype)

            usage:
              neoswitch [global opts] <command> [args]

            commands:
              list [--all]            list matching raw-HID devices (--all: every HID device)
              info                    print connected keyboard + all profiles
              get                     print current profile index
              switch <idx>            change to profile <idx>
              probe                   run transport + protocol checks (VIA 0x01, then D0 B0)
              raw <hex bytes>         send arbitrary command, print reply  (e.g. 'raw D0 B0')
              watch --app <exe>...    foreground-driven profile switch
                                      (--fg/--bg/--switch-delay/--dry-run/--log-filtered)

            global options:
              --vid <hex>             restrict to this USB vendor ID    (e.g. 0x1ea7)
              --pid <hex>             restrict to this USB product ID
              -d, --device <idx>      pick by index when multiple match (see 'list')
              --timeout <ms>          HID read timeout (default 1500)
              -v, --verbose           print every HID TX/RX in hex

            examples:
              neoswitch list
              neoswitch list --all
              neoswitch probe
              neoswitch -v info
              neoswitch --device 0 info               # disambiguate if 'list' shows 2+
              neoswitch --vid 0x1ea7 raw D0 B6        # profile count
              neoswitch switch 1
              neoswitch watch --app valorant.exe --app cs2.exe --fg 1 --bg 0
              neoswitch watch --app notepad.exe --dry-run
              neoswitch watch --app cs2.exe --fg 1 --bg 0 --switch-delay 300
              neoswitch watch --app notepad.exe --log-filtered  # show filtered windows
            """);
    }
}
