namespace Korty.Core.Hid;

/// <summary>
/// Descriptor-level facts about one HID top-level collection. Obtained WITHOUT sending anything to the device
/// (zero-access handle + HidP_GetCaps). Contains no serial numbers.
/// </summary>
public sealed record HidInterfaceInfo
{
    public const string SimulatedPrefix = "sim://";

    public required string Path { get; init; }
    public required ushort VendorId { get; init; }
    public required ushort ProductId { get; init; }
    public ushort VersionNumber { get; init; }
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
    /// <summary>Lengths in bytes INCLUDING the leading report-ID byte, as returned by HidP_GetCaps.</summary>
    public int InputReportLength { get; init; }
    public int OutputReportLength { get; init; }
    public int FeatureReportLength { get; init; }
    public string? ProductName { get; init; }
    public string? Manufacturer { get; init; }
    /// <summary>Windows container id — identical for all interfaces of one physical USB device.</summary>
    public string? ContainerId { get; init; }
    /// <summary>MI_xx from the device path (USB interface number), if present.</summary>
    public int? InterfaceNumber { get; init; }
    /// <summary>COLxx from the device path (top-level collection), if present.</summary>
    public int? CollectionNumber { get; init; }
    /// <summary>False when the descriptor could not be read (e.g. access denied) — lengths/usages are then unknown.</summary>
    public bool DescriptorRead { get; init; } = true;

    public bool IsSimulated => Path.StartsWith(SimulatedPrefix, StringComparison.Ordinal);
    public string VidPid => $"{VendorId:X4}:{ProductId:X4}";
    public string UsageLabel => $"{UsagePage:X4}/{Usage:X4}";
    public bool IsVendorDefined => UsagePage >= 0xFF00;

    /// <summary>Generic Desktop keyboard/mouse/etc. or Consumer — interfaces owned by Windows input stack.</summary>
    public bool IsStandardInput => UsagePage is 0x0001 or 0x000C;

    /// <summary>Stable across re-plugging (no instance ids). Used by snapshots and the differential analyzer.</summary>
    public string Fingerprint =>
        $"{VidPid}|MI{(InterfaceNumber?.ToString("00") ?? "--")}|COL{(CollectionNumber?.ToString("00") ?? "--")}|{UsageLabel}|{InputReportLength}/{OutputReportLength}/{FeatureReportLength}";
}
