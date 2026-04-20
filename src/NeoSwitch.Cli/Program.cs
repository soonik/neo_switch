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
            "list"   => CmdList(opts),
            "info"   => CmdInfo(opts),
            "get"    => CmdGet(opts),
            "switch" => CmdSwitch(opts, rest),
            _        => Fail($"unknown command '{cmd}' — run with --help for usage.")
        };
    }

    // ---------------- commands ----------------

    static int CmdList(Options opts)
    {
        var devs = KeyboardClient.FindAll(opts.VendorId, opts.ProductId);
        if (devs.Count == 0)
        {
            Console.WriteLine("no QwertyKeys (raw-HID, usage FF60:0061) devices found.");
            return 0;
        }
        Console.WriteLine($"{devs.Count} device(s):");
        foreach (var d in devs)
        {
            string name = Try(() => d.GetProductName()) ?? "(no name)";
            string mfg  = Try(() => d.GetManufacturer()) ?? "(no mfg)";
            Console.WriteLine($"  vid=0x{d.VendorID:X4} pid=0x{d.ProductID:X4}  {mfg} / {name}");
            Console.WriteLine($"    path: {d.DevicePath}");
        }
        return 0;
    }

    static int CmdInfo(Options opts)
    {
        using var kb = MustOpen(opts);
        Console.WriteLine($"Connected: {kb.Manufacturer} / {kb.ProductName}");
        Console.WriteLine($"  vid=0x{kb.VendorId:X4} pid=0x{kb.ProductId:X4}");

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

    // ---------------- helpers ----------------

    static KeyboardClient MustOpen(Options opts)
    {
        var kb = KeyboardClient.OpenFirst(opts.VendorId, opts.ProductId)
                 ?? throw new InvalidOperationException(
                     "no matching keyboard found. Try 'neoswitch list' to see what's connected.");
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

    // ---------------- options ----------------

    sealed record Options(int? VendorId, int? ProductId);

    static Options ParseOptions(ref string[] args)
    {
        int? vid = null, pid = null;
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
                default:
                    remaining.Add(args[i]); break;
            }
        }
        args = remaining.ToArray();
        return new Options(vid, pid);
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
              neoswitch [--vid 0xNNNN] [--pid 0xNNNN] <command> [args]

            commands:
              list              list matching HID devices
              info              print connected keyboard + all profiles
              get               print current profile index
              switch <idx>      change to profile <idx>

            options:
              --vid <hex>       restrict to this USB vendor ID
              --pid <hex>       restrict to this USB product ID

            examples:
              neoswitch list
              neoswitch info
              neoswitch switch 1
              neoswitch --vid 0x1ea7 switch 0
            """);
    }
}
