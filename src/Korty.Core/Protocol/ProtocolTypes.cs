using Korty.Core.Hid;

namespace Korty.Core.Protocol;

/// <summary>
/// The open-source projects describe two mutually INCOMPATIBLE wire protocols for 0C45:8009/800A. Which one a given
/// hardware/firmware revision speaks has NOT been established on the owner's device. See docs/PROTOCOL_RESEARCH.md.
/// </summary>
public enum ProtocolFamily
{
    /// <summary>"AA-frame": 64-byte output reports, magic 0xAA, command id in byte 1. Source: wsclx/ak820pro-modder (MIT), fw 1.07 ISO-DE, macOS.</summary>
    AaFrame,
    /// <summary>"04-feature": 64-byte feature reports <c>04 cmd sub …</c> on control iface, 4096-byte output reports for image data. Source: kabelklaus/ajazz-ak820-pro (MIT), PID 800A confirmed.</summary>
    Feature04,
}

public enum CommandRisk
{
    /// <summary>A request that asks the device to report something. Still a HID write, therefore still gated.</summary>
    ReadRequest,
    /// <summary>Changes live behaviour; firmware persistence unknown (treated as possibly persistent).</summary>
    VolatileSetting,
    /// <summary>Writes flash / saved configuration.</summary>
    PersistentSetting,
    /// <summary>Factory reset, bootloader, key remap etc. NEVER sendable from this application.</summary>
    Destructive,
    /// <summary>Not understood. NEVER sendable.</summary>
    Unknown,
}

public enum EvidenceLevel
{
    /// <summary>Seen in one open-source project only; no hardware test by this project.</summary>
    SingleSource,
    /// <summary>Two sources describe it but disagree on details.</summary>
    Conflicting,
    /// <summary>Two or more sources agree. Still not tested on the owner's keyboard.</summary>
    Agreed,
    /// <summary>Works in the reference web configurator (kabelklaus, WebHID) on the owner's keyboard in USB mode (owner-confirmed 2026-10-08). KORTY itself has not yet been tested on hardware.</summary>
    ReferenceVerified,
    /// <summary>Recovered from the vendor software's own code/data by static analysis (nothing executed); not yet tested on hardware by KORTY.</summary>
    VendorAnalysis,
    /// <summary>Tested on the owner's keyboard and recorded in verified-protocols.json. (Nothing is at this level in v0.2.)</summary>
    HardwareVerified,
}

/// <summary>Which HID interface a packet belongs on.</summary>
public enum TargetRole { Control, Data }

public sealed record CommandDescriptor(
    ProtocolFamily Family,
    int Id,
    string Name,
    CommandRisk Risk,
    EvidenceLevel Evidence,
    string Source,
    string Notes)
{
    /// <summary>Destructive and Unknown commands are rejected by <see cref="Safety.WriteGate"/> regardless of any other setting.</summary>
    public bool NeverSendable => Risk is CommandRisk.Destructive or CommandRisk.Unknown;
}

/// <summary>One report ready for the wire. <see cref="Body"/> excludes the report-id byte (Windows needs it prepended).</summary>
public sealed record ProtocolPacket(
    ProtocolFamily Family,
    int CommandId,
    string CommandName,
    ReportKind Kind,
    TargetRole Target,
    byte ReportId,
    byte[] Body,
    CommandRisk Risk,
    string Description,
    string? Assumption = null)
{
    /// <summary>Activation stage this packet belongs to ("rgb", "png", "gif"). Real hardware refuses packets without a stage.</summary>
    public string? Stage { get; init; }
    /// <summary>Feature reports only: after the set, wait this long, then read the feature report back (the reference's "handshake"). null = no read.</summary>
    public int? HandshakeAfterMs { get; init; }
    /// <summary>Pause after the packet (and its handshake) before the next one.</summary>
    public int SettleMs { get; init; }
    /// <summary>Output reports only: wait up to this long for an input report (ACK) after the write. 0 = do not wait.</summary>
    public int AckWaitMs { get; init; }

    public int WireLength => Body.Length + 1;

    public byte[] ToWireBuffer()
    {
        var buf = new byte[Body.Length + 1];
        buf[0] = ReportId;
        Buffer.BlockCopy(Body, 0, buf, 1, Body.Length);
        return buf;
    }
}
