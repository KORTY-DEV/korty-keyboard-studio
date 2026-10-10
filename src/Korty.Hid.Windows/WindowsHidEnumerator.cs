using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Korty.Core.Hid;
using Microsoft.Win32.SafeHandles;

namespace Korty.Hid.Windows;

/// <summary>
/// READ-ONLY discovery. For VID 0C45 interfaces it opens the device with ZERO desired access (query-only), reads the
/// HID preparsed data / capabilities / attributes / product strings, and closes it. It never calls WriteFile,
/// HidD_SetFeature, HidD_SetOutputReport, ReadFile, HidD_GetFeature or any other report transfer. Interfaces of every
/// other vendor are listed from their path only and are NOT opened at all.
/// </summary>
public sealed partial class WindowsHidEnumerator : IHidEnumerator
{
    private const ushort TargetVid = 0x0C45;
    private const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [GeneratedRegex(@"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase)] private static partial Regex VidPidRx();
    [GeneratedRegex(@"&mi_([0-9a-f]{2})", RegexOptions.IgnoreCase)] private static partial Regex MiRx();
    [GeneratedRegex(@"&col([0-9a-f]{2})", RegexOptions.IgnoreCase)] private static partial Regex ColRx();

    [StructLayout(LayoutKind.Sequential)] private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DEVPROPKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Sequential)] private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder buf, int len);
    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HidD_GetManufacturerString(SafeFileHandle h, StringBuilder buf, int len);

    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, ref SP_DEVINFO_DATA devInfo);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA devInfo, ref DEVPROPKEY key, out uint type, byte[] buf, uint size, out uint required, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    private static readonly DEVPROPKEY ContainerIdKey = new() { fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };

    public IReadOnlyList<HidInterfaceInfo> Enumerate()
    {
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed");
        var result = new List<HidInterfaceInfo>();
        try
        {
            for (uint index = 0; index < 1024; index++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data)) break;
                var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint size, ref info);
                if (size < 8 || size > 65536) continue;
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on 64-bit, 6 on 32-bit.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, size, out _, ref info)) continue;
                    var path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var m = VidPidRx().Match(path);
                    if (!m.Success) continue;
                    ushort vid = Convert.ToUInt16(m.Groups[1].Value, 16), pid = Convert.ToUInt16(m.Groups[2].Value, 16);
                    int? mi = MiRx().Match(path) is { Success: true } mm ? Convert.ToInt32(mm.Groups[1].Value, 16) : null;
                    int? col = ColRx().Match(path) is { Success: true } cm ? Convert.ToInt32(cm.Groups[1].Value, 16) : null;

                    if (vid != TargetVid)
                    {
                        // Foreign device: counted only. NOT opened.
                        result.Add(new HidInterfaceInfo { Path = path, VendorId = vid, ProductId = pid, InterfaceNumber = mi, CollectionNumber = col, DescriptorRead = false });
                        continue;
                    }
                    result.Add(Describe(set, ref info, path, vid, pid, mi, col));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    private static HidInterfaceInfo Describe(IntPtr set, ref SP_DEVINFO_DATA devInfo, string path, ushort vid, ushort pid, int? mi, int? col)
    {
        string? container = null;
        var buf = new byte[16];
        var key = ContainerIdKey;
        if (SetupDiGetDeviceProperty(set, ref devInfo, ref key, out _, buf, 16, out _, 0))
            container = new Guid(buf).ToString("N");

        var basic = new HidInterfaceInfo { Path = path, VendorId = vid, ProductId = pid, InterfaceNumber = mi, CollectionNumber = col, ContainerId = container, DescriptorRead = false };
        // desiredAccess = 0 → "query" access: allows descriptor queries, forbids reading/writing reports.
        using var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return basic;
        if (!HidD_GetPreparsedData(h, out var pre)) return basic;
        try
        {
            if (HidP_GetCaps(pre, out var caps) < 0) return basic;
            var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            ushort version = HidD_GetAttributes(h, ref attr) ? attr.VersionNumber : (ushort)0;
            return basic with
            {
                VersionNumber = version,
                UsagePage = caps.UsagePage, Usage = caps.Usage,
                InputReportLength = caps.InputReportByteLength,
                OutputReportLength = caps.OutputReportByteLength,
                FeatureReportLength = caps.FeatureReportByteLength,
                ProductName = ReadString(h, HidD_GetProductString),
                Manufacturer = ReadString(h, HidD_GetManufacturerString),
                DescriptorRead = true,
            };
        }
        finally { HidD_FreePreparsedData(pre); }
    }

    private delegate bool StringGetter(SafeFileHandle h, StringBuilder b, int len);
    private static string? ReadString(SafeFileHandle h, StringGetter getter)
    {
        try
        {
            var sb = new StringBuilder(256);
            return getter(h, sb, 512) ? sb.ToString().Trim() : null;
        }
        catch { return null; }
    }
}
