using Korty.Core.Hid;

namespace Korty.Core.Discovery;

public enum ConnectionKind
{
    /// <summary>Candidate: wired USB keyboard (PID 8009/800A).</summary>
    UsbCable,
    /// <summary>Candidate: 2.4 GHz receiver (PID FDFD — observed by the owner, not described by any public source).</summary>
    Wireless24Receiver,
    /// <summary>0C45:7140 — ISP bootloader. Never written to.</summary>
    Bootloader,
    /// <summary>0C45:FEFE — Bluetooth. Out of scope.</summary>
    Bluetooth,
    /// <summary>VID 0C45 but not a known PID (webcams etc. use this VID too).</summary>
    UnknownSonix,
}

public enum InterfaceRole
{
    /// <summary>Keyboard/mouse/consumer collections owned by Windows.</summary>
    SystemInput,
    /// <summary>Vendor-defined collection: the only kind a control protocol could use.</summary>
    VendorDefined,
    Unreadable,
}

public sealed record ClassifiedInterface(HidInterfaceInfo Info, InterfaceRole Role, string Hint);

/// <summary>A group of interfaces that belong to one physical USB device (same Windows container id).</summary>
public sealed record PhysicalDevice(
    string Key,
    ushort VendorId,
    ushort ProductId,
    ConnectionKind Kind,
    string Title,
    IReadOnlyList<ClassifiedInterface> Interfaces,
    IReadOnlyList<string> Notes)
{
    public string VidPid => $"{VendorId:X4}:{ProductId:X4}";
    public int VendorInterfaceCount => Interfaces.Count(i => i.Role == InterfaceRole.VendorDefined);
    public bool IsActionable => Kind is ConnectionKind.UsbCable or ConnectionKind.Wireless24Receiver;
}

public sealed record DeviceTopology(IReadOnlyList<PhysicalDevice> Devices, int TotalHidInterfaces, int IgnoredOtherVendorInterfaces)
{
    public static readonly DeviceTopology Empty = new(Array.Empty<PhysicalDevice>(), 0, 0);
    public PhysicalDevice? Usb => Devices.FirstOrDefault(d => d.Kind == ConnectionKind.UsbCable);
    public PhysicalDevice? Wireless => Devices.FirstOrDefault(d => d.Kind == ConnectionKind.Wireless24Receiver);
    public bool HasBootloader => Devices.Any(d => d.Kind == ConnectionKind.Bootloader);
}

/// <summary>
/// Separates the keyboard, the radio receiver and unrelated HID devices. Classification is by VID/PID table + grouping
/// by container id and is only ever a CANDIDATE: no byte has been exchanged with the device. Only VID 0C45 devices
/// are considered at all; every other HID device is counted and ignored.
/// </summary>
public static class DeviceClassifier
{
    public const ushort SonixVid = 0x0C45;

    public static DeviceTopology Classify(IEnumerable<HidInterfaceInfo> all)
    {
        var list = all.ToList();
        var sonix = list.Where(i => i.VendorId == SonixVid).ToList();
        var groups = sonix.GroupBy(i => (i.ContainerId ?? "none", i.ProductId));
        var devices = new List<PhysicalDevice>();
        foreach (var g in groups.OrderBy(g => g.Key.ProductId))
        {
            var pid = g.Key.ProductId;
            var kind = pid switch
            {
                0x8009 or 0x800A => ConnectionKind.UsbCable,
                0xFDFD => ConnectionKind.Wireless24Receiver,
                0x7140 => ConnectionKind.Bootloader,
                0xFEFE => ConnectionKind.Bluetooth,
                _ => ConnectionKind.UnknownSonix,
            };
            var notes = new List<string>();
            switch (kind)
            {
                case ConnectionKind.UsbCable:
                    notes.Add("AK820 Pro по USB-кабелю: интерфейсы управления FF13 и данных FF68 (подтверждено чтением дескрипторов).");
                    break;
                case ConnectionKind.Wireless24Receiver:
                    notes.Add("PID FDFD не описан ни в одном из изученных источников; соответствие 2.4 ГГц получено только из ваших наблюдений.");
                    notes.Add("kabelklaus: по 2.4 ГГц vendor-интерфейсы не доступны для настройки экрана/подсветки.");
                    break;
                case ConnectionKind.Bootloader:
                    notes.Add("Режим загрузчика ISP. Приложение никогда не записывает в такое устройство.");
                    break;
                case ConnectionKind.UnknownSonix:
                    notes.Add("VID 0C45 используют и другие устройства (например веб-камеры). Не считается клавиатурой.");
                    break;
            }
            var interfaces = g.OrderBy(i => i.InterfaceNumber ?? 99).ThenBy(i => i.CollectionNumber ?? 99)
                .Select(Describe).ToList();
            var title = kind switch
            {
                ConnectionKind.UsbCable => "AK820 Pro — USB-C",
                ConnectionKind.Wireless24Receiver => "Приёмник 2.4 GHz",
                ConnectionKind.Bootloader => "Загрузчик (ISP) — не трогать",
                ConnectionKind.Bluetooth => "Bluetooth (не поддерживается)",
                _ => "Неопознанное устройство 0C45",
            };
            var first = g.First();
            devices.Add(new PhysicalDevice($"{first.VidPid}/{g.Key.Item1}", SonixVid, pid, kind, title, interfaces, notes));
        }
        return new DeviceTopology(devices, list.Count, list.Count - sonix.Count);
    }

    private static ClassifiedInterface Describe(HidInterfaceInfo i)
    {
        if (!i.DescriptorRead) return new(i, InterfaceRole.Unreadable, "дескриптор не прочитан");
        if (!i.IsVendorDefined)
            return new(i, InterfaceRole.SystemInput, i.UsagePage == 1 ? "системный ввод (Windows)" : "потребительские/системные клавиши (Windows)");
        return new(i, InterfaceRole.VendorDefined, VendorHint(i));
    }

    /// <summary>What public sources say about this usage page. Descriptions CONFLICT; none are hardware-verified here.</summary>
    public static string VendorHint(HidInterfaceInfo i) => i.UsagePage switch
    {
        0xFF68 => "FF68: wsclx — управление (64 Б, output); kabelklaus — данные экрана (4096 Б output)",
        0xFF67 => "FF67: wsclx — загрузка TFT (4104 Б); тот же источник: молча отбрасывает команды управления",
        0xFF13 => "FF13: kabelklaus — управление (64 Б feature)",
        0xFF60 when i.Usage == 0x61 => "FF60/0061: стандартная usage-пара «raw HID» QMK/VIA. НЕ описана в wsclx/kabelklaus; назначение на этом устройстве не подтверждено (наблюдалась на донгле FDFD)",
        0xFFFF => "FFFF: vendor-вход (в изученных источниках не описан)",
        _ => $"{i.UsageLabel}: в изученных источниках не описан",
    } + (i.OutputReportLength > 0 || i.FeatureReportLength > 0 ? $" · фактически out={i.OutputReportLength} feat={i.FeatureReportLength}" : "");
}
