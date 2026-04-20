namespace NeoSwitch.Core;

/// <summary>
/// Wire-level opcodes for the QwertyKeys HID protocol.
/// Reverse-engineered from he.qwertykeys.com — see SPEC.md.
///
/// Every command is a 32-byte output report on report ID 0.
/// Profile-related commands are wrapped by the custom 0xD0 "actuation"
/// namespace: [0xD0, subOp, ...args].
/// </summary>
public static class Protocol
{
    /// <summary>Raw-HID interface fingerprint (QMK default). Usage page &lt;&lt; 16 | usage.</summary>
    public const uint RawHidUsage = 0xFF60_0061u;
    public const ushort RawHidUsagePage = 0xFF60;
    public const ushort RawHidUsageId = 0x0061;

    /// <summary>On-wire data length of a single report (excluding the report-ID byte).</summary>
    public const int ReportDataSize = 32;

    /// <summary>Report ID used for all traffic on the raw-HID interface.</summary>
    public const byte ReportId = 0x00;

    /// <summary>Base VIA opcode for the QwertyKeys "actuation" extension. Followed by a sub-opcode.</summary>
    public const byte CmdCustomActuation = 0xD0;

    /// <summary>Profile sub-opcodes under 0xD0. See SPEC §3.2.</summary>
    public static class Profile
    {
        public const byte GetIdx   = 0xB0; // reply: [D0, B0, idx]
        public const byte SetIdx   = 0xB1; // payload: [idx]
        public const byte GetName  = 0xB2; // payload: [i]  reply: 28 bytes UTF-8, null-padded
        public const byte SetName  = 0xB3; // payload: [i, name...28]
        public const byte GetColor = 0xB4; // payload: [i]  reply: [..., r, g, b]
        public const byte SetColor = 0xB5; // payload: [i, r, g, b]
        public const byte GetCount = 0xB6; // reply: [D0, B6, count]

        public const int NameSize = 28;
    }
}
