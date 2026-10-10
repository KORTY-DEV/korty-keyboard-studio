using System.ComponentModel;
using System.Runtime.InteropServices;
using Korty.Core.Hid;
using Korty.Core.Safety;
using Microsoft.Win32.SafeHandles;

namespace Korty.Hid.Windows;

/// <summary>
/// THE ONLY FILE IN THE SOLUTION THAT MAY TRANSMIT TO / READ REPORTS FROM A DEVICE (enforced by SourceAuditTests).
/// <see cref="WindowsHidIo"/> performs the raw Win32 calls; <see cref="WindowsHidTransport"/> wraps it in the
/// platform-independent <see cref="HidTransportCore"/>, which accepts nothing but <see cref="ApprovedWrite"/> tokens minted
/// by <c>WriteGate</c>. Without activated stages in verified-protocols.json AND session arming the gate approves nothing for
/// real hardware, so this code is inert.
///
/// WebHID → Windows differences handled here (the reference configurator runs on WebHID):
///  * WebHID passes (reportId, data); Windows needs ONE buffer = reportId + data. Report id is 0x00, so a 64-byte feature
///    payload is a 65-byte buffer (= FeatureReportByteLength) and a 4096-byte chunk is a 4097-byte buffer (= OutputReportByteLength).
///  * receiveFeatureReport(0) → HidD_GetFeature with a 65-byte buffer whose first byte is 0.
///  * the "inputreport" event used as ACK → a timed overlapped ReadFile; queued stale reports are flushed first (HidD_FlushQueue)
///    because WebHID only delivers events to a listener that exists at the time of arrival.
/// </summary>
public sealed class WindowsHidTransport : IHidTransport, IDisposable
{
    private readonly WindowsHidIo _io = new();
    private readonly HidTransportCore _core;

    public WindowsHidTransport() { _core = new HidTransportCore(_io); }

    public Task<TransportResult> SendAsync(ApprovedWrite write, CancellationToken cancellationToken) => _core.SendAsync(write, cancellationToken);

    public void Dispose() => _core.Dispose();
}

public sealed class WindowsHidIo : IHidDeviceIo
{
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 0x102;
    private const int ERROR_IO_PENDING = 997, ERROR_OPERATION_ABORTED = 995;
    // The first chunk after IMAGE_CFG can stay pending while the keyboard erases flash for the WHOLE image (grows with frame count);
    // WebHID in the reference has no timeout at all. Cancelling a half-done write corrupts the transfer, so wait long.
    private const int WriteTimeoutMs = 60000;

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED { public IntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr hEvent; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int count, IntPtr written, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int count, IntPtr read, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out int transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateEvent(IntPtr attrs, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initial, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HidD_FlushQueue(SafeFileHandle h);

    /// <summary>Win32 error with the numeric code AND system text in the message (the journal must show the exact code).</summary>
    private static Win32Exception W32(string op, int code) =>
        new(code, $"{op}: Win32 {code} (0x{code:X}) — {new Win32Exception(code).Message}");

    private readonly Dictionary<string, SafeFileHandle> _handles = new();
    private readonly object _lock = new();

    private SafeFileHandle Handle(string path)
    {
        lock (_lock)
        {
            if (_handles.TryGetValue(path, out var existing) && !existing.IsClosed) return existing;
            var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) throw W32("Не удалось открыть HID-интерфейс (закрыто ли фирменное ПО AJAZZ?)", Marshal.GetLastWin32Error());
            _handles[path] = h;
            return h;
        }
    }

    private void Forget(string path) { lock (_lock) { if (_handles.Remove(path, out var h)) h.Dispose(); } }

    public void SetFeature(string path, byte[] bufferWithReportId)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!HidD_SetFeature(Handle(path), bufferWithReportId, bufferWithReportId.Length))
                    throw W32("HidD_SetFeature", Marshal.GetLastWin32Error());
                return;
            }
            catch
            {
                Forget(path);                    // never reuse a handle that just failed
                if (attempt >= 1) throw;         // one retry on a freshly opened handle, then report the real error
                Thread.Sleep(150);
            }
        }
    }

    public byte[]? GetFeature(string path, int lengthWithReportId)
    {
        var buf = new byte[lengthWithReportId];
        buf[0] = 0x00; // report id
        return HidD_GetFeature(Handle(path), buf, buf.Length) ? buf : null;
    }

    public void WriteOutput(string path, byte[] bufferWithReportId)
    {
        try
        {
            var h = Handle(path);
            var ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            // The kernel keeps using the buffer and the OVERLAPPED until the I/O completes: both must stay put (pinned) for the whole operation.
            var pin = GCHandle.Alloc(bufferWithReportId, GCHandleType.Pinned);
            var ovMem = Marshal.AllocHGlobal(Marshal.SizeOf<OVERLAPPED>());
            try
            {
                Marshal.StructureToPtr(new OVERLAPPED { hEvent = ev }, ovMem, false);
                if (!WriteFile(h, pin.AddrOfPinnedObject(), bufferWithReportId.Length, IntPtr.Zero, ovMem))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_IO_PENDING) throw W32("WriteFile", err);
                    if (WaitForSingleObject(ev, WriteTimeoutMs) != WAIT_OBJECT_0)
                    {
                        CancelIoEx(h, ovMem);
                        GetOverlappedResult(h, ovMem, out _, true);     // wait until the cancelled request is really finished before unpinning
                        throw new TimeoutException($"WriteFile: устройство не приняло отчёт за {WriteTimeoutMs / 1000} с (операция отменена, Win32 {ERROR_OPERATION_ABORTED})");
                    }
                }
                if (!GetOverlappedResult(h, ovMem, out int written, false)) throw W32("GetOverlappedResult", Marshal.GetLastWin32Error());
                if (written != bufferWithReportId.Length) throw new IOException($"Неполная запись {written}/{bufferWithReportId.Length} байт");
            }
            finally { pin.Free(); Marshal.FreeHGlobal(ovMem); CloseHandle(ev); }
        }
        catch { Forget(path); throw; }
    }

    public byte[]? ReadInput(string path, int lengthWithReportId, int timeoutMs)
    {
        var h = Handle(path);
        var buf = new byte[lengthWithReportId];
        var ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
        var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        var ovMem = Marshal.AllocHGlobal(Marshal.SizeOf<OVERLAPPED>());
        try
        {
            Marshal.StructureToPtr(new OVERLAPPED { hEvent = ev }, ovMem, false);
            if (!ReadFile(h, pin.AddrOfPinnedObject(), buf.Length, IntPtr.Zero, ovMem))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING) throw W32("ReadFile", err);
                if (WaitForSingleObject(ev, (uint)timeoutMs) == WAIT_TIMEOUT)
                {
                    CancelIoEx(h, ovMem);
                    GetOverlappedResult(h, ovMem, out _, true);          // let the cancelled request finish before the buffer is released
                    return null;
                }
            }
            if (!GetOverlappedResult(h, ovMem, out _, false))
            {
                int err = Marshal.GetLastWin32Error();
                return err == ERROR_OPERATION_ABORTED ? null : throw W32("GetOverlappedResult", err);
            }
            return buf;
        }
        finally { pin.Free(); Marshal.FreeHGlobal(ovMem); CloseHandle(ev); }
    }

    public void FlushInput(string path)
    {
        try { HidD_FlushQueue(Handle(path)); } catch (Win32Exception) { /* best effort */ }
    }

    public void Dispose()
    {
        lock (_lock) { foreach (var h in _handles.Values) h.Dispose(); _handles.Clear(); }
    }
}
