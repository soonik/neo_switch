using System.Text;
using HidSharp;

namespace NeoSwitch.Core;

/// <summary>
/// Thin wrapper around a QwertyKeys keyboard speaking the raw-HID protocol
/// documented in SPEC.md. One instance owns one open HID stream.
/// Mirror of the site's <c>HIDAPI</c>/<c>ActuationApi</c> classes.
/// </summary>
public sealed class KeyboardClient : IDisposable
{
    private readonly HidDevice _device;
    private readonly HidStream _stream;
    private readonly int _outLen;
    private readonly int _inLen;
    private readonly int _writeReportIdOffset;
    private readonly object _lock = new();

    /// <summary>Optional wire-level tracer; receives hex-formatted "TX"/"RX" lines.</summary>
    public Action<string>? Logger { get; set; }

    /// <summary>Per-round-trip read timeout (default 1500 ms).</summary>
    public int ReadTimeoutMs
    {
        get => _stream.ReadTimeout;
        set => _stream.ReadTimeout = value;
    }

    public HidDevice Device => _device;
    public string ProductName => SafeGet(() => _device.GetProductName()) ?? "(unknown)";
    public string Manufacturer => SafeGet(() => _device.GetManufacturer()) ?? "(unknown)";
    public int VendorId => _device.VendorID;
    public int ProductId => _device.ProductID;
    public string DevicePath => _device.DevicePath;
    public int MaxOutputReportLength => _outLen;
    public int MaxInputReportLength => _inLen;

    private KeyboardClient(HidDevice device, HidStream stream)
    {
        _device = device;
        _stream = stream;
        _outLen = device.GetMaxOutputReportLength();
        _inLen = device.GetMaxInputReportLength();
        // Windows always prepends the report-ID byte to the write buffer.
        _writeReportIdOffset = _outLen >= Protocol.ReportDataSize + 1 ? 1 : 0;
        _stream.ReadTimeout = 1500;
    }

    // --------------- Discovery ---------------

    /// <summary>Return every HID device that exposes the QMK raw-HID interface.</summary>
    public static IReadOnlyList<HidDevice> FindAll(int? vendorId = null, int? productId = null)
    {
        var result = new List<HidDevice>();
        foreach (var dev in DeviceList.Local.GetHidDevices())
        {
            if (vendorId is int v && dev.VendorID != v) continue;
            if (productId is int p && dev.ProductID != p) continue;
            if (HasRawHidInterface(dev)) result.Add(dev);
        }
        return result;
    }

    public static KeyboardClient Open(HidDevice device)
    {
        if (!device.TryOpen(out HidStream stream))
            throw new IOException($"Failed to open HID device '{device.DevicePath}'. " +
                                   "Another app (e.g. the QwertyKeys web configurator) may hold the handle.");
        return new KeyboardClient(device, stream);
    }

    public static KeyboardClient? OpenFirst(int? vendorId = null, int? productId = null)
    {
        var devices = FindAll(vendorId, productId);
        return devices.Count == 0 ? null : Open(devices[0]);
    }

    private static bool HasRawHidInterface(HidDevice device)
    {
        foreach (uint usage in GetUsages(device))
            if (usage == Protocol.RawHidUsage) return true;
        return false;
    }

    /// <summary>All HID devices on the system (unfiltered). For diagnostics.</summary>
    public static IReadOnlyList<HidDevice> AllHidDevices()
        => DeviceList.Local.GetHidDevices().ToArray();

    /// <summary>Return every top-level application usage (page &lt;&lt; 16 | id) declared by the device, safely.</summary>
    public static IReadOnlyList<uint> GetUsages(HidDevice device)
    {
        var result = new List<uint>();
        try
        {
            var rd = device.GetReportDescriptor();
            foreach (var di in rd.DeviceItems)
                foreach (uint usage in di.Usages.GetAllValues())
                    result.Add(usage);
        }
        catch
        {
            // Some collections refuse descriptor queries — return what we have.
        }
        return result;
    }

    // --------------- High-level API ---------------

    public byte GetCurrentProfileIdx()
    {
        var reply = SendAndReceive(stackalloc byte[] { Protocol.CmdCustomActuation, Protocol.Profile.GetIdx });
        return reply[2];
    }

    public byte GetProfileCount()
    {
        var reply = SendAndReceive(stackalloc byte[] { Protocol.CmdCustomActuation, Protocol.Profile.GetCount });
        return reply[2];
    }

    public void SwitchProfile(byte idx)
    {
        SendAndReceive(stackalloc byte[] { Protocol.CmdCustomActuation, Protocol.Profile.SetIdx, idx });
    }

    public string GetProfileName(byte idx)
    {
        var reply = SendAndReceive(stackalloc byte[] { Protocol.CmdCustomActuation, Protocol.Profile.GetName, idx });
        // reply layout: [D0, B2, idx, name bytes (28), ...]
        const int start = 3;
        int end = Math.Min(reply.Length, start + Protocol.Profile.NameSize);
        int actualEnd = start;
        for (int i = start; i < end && reply[i] != 0; i++) actualEnd = i + 1;
        return Encoding.UTF8.GetString(reply, start, actualEnd - start);
    }

    public RgbColor GetProfileColor(byte idx)
    {
        var reply = SendAndReceive(stackalloc byte[] { Protocol.CmdCustomActuation, Protocol.Profile.GetColor, idx });
        return new RgbColor(reply[3], reply[4], reply[5]);
    }

    public IReadOnlyList<ProfileInfo> LoadAllProfiles()
    {
        byte count = GetProfileCount();
        var list = new List<ProfileInfo>(count);
        for (byte i = 0; i < count; i++)
        {
            list.Add(new ProfileInfo(i, GetProfileName(i), GetProfileColor(i)));
        }
        return list;
    }

    // --------------- Transport ---------------

    /// <summary>
    /// Write an arbitrary command (e.g. VIA <c>0x01</c> protocol version) and
    /// read the first reply whose first <paramref name="matchLen"/> bytes echo
    /// <paramref name="command"/>. Returns the report payload with the HID
    /// report-ID byte stripped.
    /// </summary>
    public byte[] SendAndReceive(ReadOnlySpan<byte> command, int matchLen = 2)
    {
        lock (_lock)
        {
            // ----- build outgoing report -----
            var outBuf = new byte[_outLen];
            command.CopyTo(outBuf.AsSpan(_writeReportIdOffset));
            Trace("TX", outBuf);

            try
            {
                _stream.Write(outBuf);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                throw new IOException(
                    $"HID write failed after {ReadTimeoutMs} ms sending {Hex(command)}. " +
                    "Is the keyboard still connected? Is the web configurator (he.qwertykeys.com) open?",
                    ex);
            }

            // ----- read replies, skipping non-matching reports -----
            var inBuf = new byte[Math.Max(_inLen, Protocol.ReportDataSize + 1)];
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int n;
                try
                {
                    n = _stream.Read(inBuf);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException(
                        $"HID read timed out after {ReadTimeoutMs} ms waiting for a reply to {Hex(command)}. " +
                        $"Device: {DevicePath}. " +
                        "Possible causes: firmware does not implement this command, wrong HID interface, " +
                        "or another app holds the handle. Try 'neoswitch probe' to diagnose.");
                }

                if (n <= 0) continue;
                int payloadOffset = n > Protocol.ReportDataSize ? 1 : 0;
                var rxView = new ReadOnlySpan<byte>(inBuf, 0, n);
                Trace("RX", rxView);

                if (EchoesCommand(inBuf, payloadOffset, command, matchLen))
                {
                    var result = new byte[n - payloadOffset];
                    Array.Copy(inBuf, payloadOffset, result, 0, result.Length);
                    return result;
                }
            }
            throw new IOException(
                $"No reply matching {Hex(command)} received after 8 reads (unsolicited reports only).");
        }
    }

    private static bool EchoesCommand(byte[] reply, int offset, ReadOnlySpan<byte> cmd, int matchLen)
    {
        int check = Math.Min(matchLen, cmd.Length);
        if (reply.Length - offset < check) return false;
        for (int i = 0; i < check; i++)
            if (reply[offset + i] != cmd[i]) return false;
        return true;
    }

    /// <summary>Read one raw HID input report (including report-ID prefix on Windows). Used for probing.</summary>
    public byte[] ReadRaw()
    {
        lock (_lock)
        {
            var buf = new byte[Math.Max(_inLen, Protocol.ReportDataSize + 1)];
            int n = _stream.Read(buf);
            var result = new byte[n];
            Array.Copy(buf, result, n);
            Trace("RX", result);
            return result;
        }
    }

    /// <summary>Send a raw command (without the report-ID byte); returns matching reply payload.</summary>
    public byte[] SendRaw(ReadOnlySpan<byte> command, int matchLen = 2)
        => SendAndReceive(command, matchLen);

    private void Trace(string tag, ReadOnlySpan<byte> bytes)
    {
        if (Logger is null) return;
        Logger($"{tag} {Hex(bytes)}");
    }

    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }

    public static byte[] ParseHex(string s)
    {
        var cleaned = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c) || c == ',' || c == ':') continue;
            if (c == '0' && cleaned.Length < s.Length - 1 &&
                (s[cleaned.Length + 1] == 'x' || s[cleaned.Length + 1] == 'X')) continue;
            if (c == 'x' || c == 'X') continue;
            cleaned.Append(c);
        }
        string clean = cleaned.ToString();
        if (clean.Length % 2 != 0)
            throw new FormatException($"hex string must have even length: '{s}'");
        var result = new byte[clean.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        return result;
    }

    private static T? SafeGet<T>(Func<T> fn) where T : class
    {
        try { return fn(); } catch { return null; }
    }

    public void Dispose() => _stream.Dispose();
}
