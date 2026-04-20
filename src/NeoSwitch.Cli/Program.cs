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
        Console.WriteLine($"{devs.Count} matching device(s):");
        foreach (var d in devs) PrintDevice(d, indent: "  ");
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

    static void PrintDevice(HidDevice d, string indent)
    {
        string name = Try(() => d.GetProductName()) ?? "(no name)";
        string mfg  = Try(() => d.GetManufacturer()) ?? "(no mfg)";
        var usages  = KeyboardClient.GetUsages(d);
        bool raw    = usages.Contains(Protocol.RawHidUsage);
        Console.WriteLine($"{indent}vid=0x{d.VendorID:X4} pid=0x{d.ProductID:X4}  {mfg} / {name}   " +
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

        if (devs.Count > 1 && !(opts.VendorId.HasValue && opts.ProductId.HasValue))
        {
            Console.Error.WriteLine($"warning: {devs.Count} raw-HID devices match; using the first one.");
            Console.Error.WriteLine("         disambiguate with --vid 0xNNNN --pid 0xNNNN.");
        }

        var kb = KeyboardClient.Open(devs[0]);
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

    sealed record Options(int? VendorId, int? ProductId, bool Verbose, int TimeoutMs);

    static Options ParseOptions(ref string[] args)
    {
        int? vid = null, pid = null;
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
        return new Options(vid, pid, verbose, timeout);
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

            global options:
              --vid <hex>             restrict to this USB vendor ID    (e.g. 0x1ea7)
              --pid <hex>             restrict to this USB product ID
              --timeout <ms>          HID read timeout (default 1500)
              -v, --verbose           print every HID TX/RX in hex

            examples:
              neoswitch list
              neoswitch list --all
              neoswitch probe
              neoswitch -v info
              neoswitch --vid 0x1ea7 raw D0 B6        # profile count
              neoswitch switch 1
            """);
    }
}
