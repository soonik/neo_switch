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

    public HidDevice Device => _device;
    public string ProductName => SafeGet(() => _device.GetProductName()) ?? "(unknown)";
    public string Manufacturer => SafeGet(() => _device.GetManufacturer()) ?? "(unknown)";
    public int VendorId => _device.VendorID;
    public int ProductId => _device.ProductID;
    public string DevicePath => _device.DevicePath;

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
        try
        {
            var rd = device.GetReportDescriptor();
            foreach (var di in rd.DeviceItems)
            {
                foreach (uint usage in di.Usages.GetAllValues())
                {
                    if (usage == Protocol.RawHidUsage) return true;
                }
            }
        }
        catch
        {
            // Some collections (boot keyboard etc.) refuse descriptor queries — ignore.
        }
        return false;
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
    /// Serialises a write + read round-trip. Retries the read up to 8 times to
    /// skip unsolicited reports until one echoes our request header.
    /// </summary>
    private byte[] SendAndReceive(ReadOnlySpan<byte> command)
    {
        lock (_lock)
        {
            var outBuf = new byte[_outLen];
            command.CopyTo(outBuf.AsSpan(_writeReportIdOffset));
            _stream.Write(outBuf);

            var inBuf = new byte[Math.Max(_inLen, Protocol.ReportDataSize + 1)];
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int n = _stream.Read(inBuf);
                if (n <= 0) continue;
                int payloadOffset = n > Protocol.ReportDataSize ? 1 : 0;
                if (EchoesCommand(inBuf, payloadOffset, command))
                {
                    var result = new byte[n - payloadOffset];
                    Array.Copy(inBuf, payloadOffset, result, 0, result.Length);
                    return result;
                }
            }
            throw new IOException("No matching HID reply received after 8 reads.");
        }
    }

    private static bool EchoesCommand(byte[] reply, int offset, ReadOnlySpan<byte> cmd)
    {
        // Match on the first 2 header bytes: [base, subOp].
        int check = Math.Min(2, cmd.Length);
        if (reply.Length - offset < check) return false;
        for (int i = 0; i < check; i++)
            if (reply[offset + i] != cmd[i]) return false;
        return true;
    }

    private static T? SafeGet<T>(Func<T> fn) where T : class
    {
        try { return fn(); } catch { return null; }
    }

    public void Dispose() => _stream.Dispose();
}
