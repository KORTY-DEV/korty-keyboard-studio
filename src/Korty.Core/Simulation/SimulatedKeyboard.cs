using Korty.Core.Hid;
using Korty.Core.Model;
using Korty.Core.Protocol;
using Korty.Core.Safety;
using Korty.Core.Tft;

namespace Korty.Core.Simulation;

public enum SimConnection { None, Usb, Wireless24, UsbAndReceiver }

/// <summary>
/// A software stand-in for the keyboard so the whole stack (UI → service → gate → transport → "device") can be exercised
/// without hardware. It implements ONLY the documented behaviour of the selected protocol family; it is not evidence
/// that the real firmware behaves the same. Interface lengths follow the public descriptions, not measurements.
/// </summary>
public sealed class SimulatedKeyboard : IHidEnumerator, IHidTransport
{
    private readonly object _lock = new();
    public ProtocolFamily Family { get; private set; }
    public SimConnection Connection { get; private set; } = SimConnection.Usb;
    /// <summary>When false (default) the receiver exposes vendor interfaces that do NOT match the control protocol (kabelklaus: no vendor control over 2.4 GHz).</summary>
    public bool WirelessControlAvailable { get; set; }
    /// <summary>Hypothesis H1 switch: the (simulated) firmware loses the user screen on every connection change.</summary>
    public bool ResetScreenOnSwitch { get; set; }
    public bool IncludeDecoys { get; set; } = true;
    public int FailAfterWrites { get; set; } = -1;

    public RgbSettings? Rgb { get; private set; }
    public TftAnimation? Screen { get; private set; }
    public bool ScreenIsBlack { get; private set; }
    public int AcceptedWrites { get; private set; }
    public List<string> Journal { get; } = new();
    public event Action? Changed;

    // transfer state
    private int _expectedChunks, _receivedChunks;
    private MemoryStream? _upload;
    private bool _expectModeData;
    private bool _imageSession;

    public SimulatedKeyboard(ProtocolFamily family = ProtocolFamily.AaFrame) { Family = family; }

    public void SetFamily(ProtocolFamily family) { lock (_lock) { Family = family; ResetTransfer(); } Changed?.Invoke(); }

    public void SetConnection(SimConnection c)
    {
        lock (_lock)
        {
            if (c == Connection) return;
            Journal.Add($"connection {Connection} → {c}");
            Connection = c;
            ResetTransfer();
            if (ResetScreenOnSwitch) { Screen = null; ScreenIsBlack = true; Journal.Add("H1: экран сброшен (чёрный) при смене подключения"); }
        }
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();

    public void ClearScreenToBlack() { lock (_lock) { Screen = null; ScreenIsBlack = true; } Changed?.Invoke(); }

    private void ResetTransfer() { _upload = null; _expectedChunks = _receivedChunks = 0; _expectModeData = false; _imageSession = false; }

    // ---------------- enumeration ----------------

    public IReadOnlyList<HidInterfaceInfo> Enumerate()
    {
        lock (_lock)
        {
            var list = new List<HidInterfaceInfo>();
            if (Connection is SimConnection.Usb or SimConnection.UsbAndReceiver) AddUsb(list);
            if (Connection is SimConnection.Wireless24 or SimConnection.UsbAndReceiver) AddReceiver(list);
            if (IncludeDecoys)
            {
                list.Add(Mk("sim://decoy/mouse", 0x046D, 0xC52B, 0x0001, 0x0002, 20, 0, 0, "decoy-mouse", 0, 0, "Simulated unrelated mouse receiver"));
                list.Add(Mk("sim://decoy/cam", 0x0C45, 0x6366, 0xFF00, 0x0001, 64, 64, 0, "decoy-cam", 1, null, "Simulated Sonix webcam (same VID!)"));
            }
            return list;
        }
    }

    private TargetRoleLens Lens() => Family == ProtocolFamily.AaFrame
        ? new(0xFF68, 65, 0, 0xFF67, 4105, 0)
        : new(0xFF13, 0, 65, 0xFF68, 4097, 0);

    private readonly record struct TargetRoleLens(ushort CtrlPage, int CtrlOut, int CtrlFeat, ushort DataPage, int DataOut, int DataFeat);

    private void AddUsb(List<HidInterfaceInfo> l)
    {
        var t = Lens();
        const string c = "sim-usb";
        l.Add(Mk("sim://usb/kbd", 0x0C45, 0x8009, 0x0001, 0x0006, 9, 1, 0, c, 0, 1, "AK820 Pro (simulated)"));
        l.Add(Mk("sim://usb/consumer", 0x0C45, 0x8009, 0x000C, 0x0001, 3, 0, 0, c, 0, 2, "AK820 Pro (simulated)"));
        l.Add(Mk("sim://usb/ctrl", 0x0C45, 0x8009, t.CtrlPage, 0x0001, 65, t.CtrlOut, t.CtrlFeat, c, 1, 5, "AK820 Pro (simulated)"));
        l.Add(Mk("sim://usb/data", 0x0C45, 0x8009, t.DataPage, (ushort)(Family == ProtocolFamily.Feature04 ? 0x0061 : 0x0001), 65, t.DataOut, t.DataFeat, c, 2, null, "AK820 Pro (simulated)"));
        l.Add(Mk("sim://usb/aux", 0x0C45, 0x8009, 0xFF00, 0x0001, 33, 0, 0, c, 3, null, "AK820 Pro (simulated)"));
    }

    private void AddReceiver(List<HidInterfaceInfo> l)
    {
        var t = Lens();
        const string c = "sim-rx";
        l.Add(Mk("sim://rx/kbd", 0x0C45, 0xFDFD, 0x0001, 0x0006, 9, 1, 0, c, 0, 1, "2.4G Receiver (simulated)"));
        if (WirelessControlAvailable)
        {
            l.Add(Mk("sim://rx/ctrl", 0x0C45, 0xFDFD, t.CtrlPage, 0x0001, 65, t.CtrlOut, t.CtrlFeat, c, 2, 5, "2.4G Receiver (simulated)"));
            l.Add(Mk("sim://rx/data", 0x0C45, 0xFDFD, t.DataPage, 0x0001, 65, t.DataOut, t.DataFeat, c, 3, null, "2.4G Receiver (simulated)"));
        }
        else
        {
            // Vendor interfaces exist (as the owner observed) but do not match the wired control protocol.
            l.Add(Mk("sim://rx/v1", 0x0C45, 0xFDFD, 0xFF02, 0x0001, 33, 33, 0, c, 2, 5, "2.4G Receiver (simulated)"));
            l.Add(Mk("sim://rx/v2", 0x0C45, 0xFDFD, 0xFF03, 0x0001, 33, 33, 0, c, 3, null, "2.4G Receiver (simulated)"));
        }
        l.Add(Mk("sim://rx/v3", 0x0C45, 0xFDFD, 0xFF00, 0x0001, 33, 0, 0, c, 4, null, "2.4G Receiver (simulated)"));
    }

    private static HidInterfaceInfo Mk(string path, ushort vid, ushort pid, ushort page, ushort usage, int inLen, int outLen, int featLen, string container, int mi, int? col, string name) => new()
    {
        Path = path, VendorId = vid, ProductId = pid, UsagePage = page, Usage = usage,
        InputReportLength = inLen, OutputReportLength = outLen, FeatureReportLength = featLen,
        ContainerId = container, InterfaceNumber = mi, CollectionNumber = col, ProductName = name,
    };

    // ---------------- transport ----------------

    public async Task<TransportResult> SendAsync(ApprovedWrite write, CancellationToken ct)
    {
        if (!write.Target.IsSimulated) return TransportResult.Fail("SimulatedKeyboard accepts only simulated interfaces");
        await Task.Delay(1, ct).ConfigureAwait(false); // keeps the UI honest about asynchronous transfers
        TransportResult result;
        lock (_lock)
        {
            if (FailAfterWrites >= 0 && AcceptedWrites >= FailAfterWrites)
                return TransportResult.Fail("simulated I/O error (FailAfterWrites)");
            bool receiver = write.Target.ProductId == 0xFDFD;
            if (receiver && !WirelessControlAvailable) return TransportResult.Fail("receiver does not forward control reports (simulated)");
            result = Family == ProtocolFamily.AaFrame ? HandleAa(write.Packet) : Handle04(write.Packet);
            if (result.Success)
            {
                AcceptedWrites++;
                // Answer like the keyboard in the reference: feature read-back echoes the command (byte 3 = 1), chunks are ACKed with an input report.
                var pk = write.Packet;
                if (pk.HandshakeAfterMs != null || pk.AckWaitMs > 0)
                {
                    var reply = new byte[65];
                    if (pk.Kind == ReportKind.Feature) { reply[1] = 0x04; reply[2] = (byte)pk.CommandId; reply[4] = 0x01; }
                    result = result with { Response = reply };
                }
            }
        }
        Changed?.Invoke();
        return result;
    }

    private TransportResult HandleAa(ProtocolPacket p)
    {
        var b = p.Body;
        if (p.Family != ProtocolFamily.AaFrame) return TransportResult.Fail("protocol family mismatch");
        if (b.Length < 8 || b[0] != 0xAA) return TransportResult.Fail("bad magic");
        switch (b[1])
        {
            case 0x23:
            {
                if (b.Length != 64 || b[8 + 14] != 0xAA || b[8 + 15] != 0x55) return TransportResult.Fail("bad LED trailer");
                var s = new RgbSettings
                {
                    Mode = b[8], Primary = new(b[9], b[10], b[11]), Secondary = new(b[13], b[14], b[15]),
                    ColorMode = b[16], Brightness = b[17], Speed = b[18], Direction = b[19],
                };
                try { s.Validate(); } catch (Exception ex) { return TransportResult.Fail(ex.Message); }
                Rgb = s; Journal.Add($"LED mode={s.Mode} {s.Primary.ToHex()} br={s.Brightness} sp={s.Speed}");
                return new TransportResult(true, p.WireLength);
            }
            case 0x50:
            {
                if (b.Length != TftUploadPlanner.Aa_HeaderLength + TftUploadPlanner.Aa_ChunkPayload) return TransportResult.Fail("bad chunk length");
                int idx = b[2] | (b[3] << 8), total = b[4] | (b[5] << 8), magic = b[6] | (b[7] << 8);
                if (magic != TftUploadPlanner.Aa_Magic) return TransportResult.Fail($"bad chunk magic 0x{magic:X4} (expected 0x0650)");
                if (idx == 0) { _upload = new MemoryStream(); _expectedChunks = total; _receivedChunks = 0; }
                if (_upload == null || idx != _receivedChunks || total != _expectedChunks) return TransportResult.Fail("chunk out of order");
                _upload.Write(b, TftUploadPlanner.Aa_HeaderLength, TftUploadPlanner.Aa_ChunkPayload);
                _receivedChunks++;
                if (_receivedChunks == _expectedChunks) Commit(delayUnitMs: 5);
                return new TransportResult(true, p.WireLength);
            }
            default:
                return TransportResult.Fail($"unsupported command 0x{b[1]:X2} in simulation");
        }
    }

    private TransportResult Handle04(ProtocolPacket p)
    {
        if (p.Family != ProtocolFamily.Feature04) return TransportResult.Fail("protocol family mismatch");
        var b = p.Body;
        if (p.Target == TargetRole.Data)
        {
            if (!_imageSession || _upload == null || b.Length != 4096) return TransportResult.Fail("unexpected image data");
            _upload.Write(b);
            _receivedChunks++;
            return new TransportResult(true, p.WireLength);
        }
        if (_expectModeData)
        {
            _expectModeData = false;
            if (b[14] != 0x55 || b[15] != 0xAA) return TransportResult.Fail("bad MODE_DATA trailer");
            var s = new RgbSettings { Mode = b[0], Primary = new(b[1], b[2], b[3]), ColorMode = b[8], Brightness = b[9], Speed = b[10], Direction = b[11] };
            try { s.Validate(); } catch (Exception ex) { return TransportResult.Fail(ex.Message); }
            Rgb = s; Journal.Add($"MODE_DATA mode={s.Mode} {s.Primary.ToHex()}");
            return new TransportResult(true, p.WireLength);
        }
        if (b.Length != 64 || b[0] != 0x04) return TransportResult.Fail("bad feature framing");
        switch (b[1])
        {
            case 0x18: _imageSession = false; return new TransportResult(true, p.WireLength);
            case 0x13: _expectModeData = true; return new TransportResult(true, p.WireLength);
            case 0x72:
                _expectedChunks = b[8] | (b[9] << 8);
                _upload = new MemoryStream(); _receivedChunks = 0; _imageSession = true;
                return new TransportResult(true, p.WireLength);
            case 0x02:
            case 0xF0:
                if (_imageSession)
                {
                    if (_receivedChunks != _expectedChunks) { ResetTransfer(); return TransportResult.Fail($"closed with {_receivedChunks}/{_expectedChunks} chunks"); }
                    Commit(delayUnitMs: 2);
                    _imageSession = false;
                }
                return new TransportResult(true, p.WireLength);
            default:
                return TransportResult.Fail($"unsupported command 0x{b[1]:X2} in simulation");
        }
    }

    private void Commit(int delayUnitMs)
    {
        try
        {
            Screen = TftPayloadBuilder.Decode(_upload!.ToArray(), delayUnitMs);
            ScreenIsBlack = false;
            Journal.Add($"TFT: принято {Screen.Frames.Count} кадр(ов), {_upload.Length} Б");
        }
        catch (Exception ex) { Journal.Add("TFT: ошибка декодирования — " + ex.Message); ScreenIsBlack = true; }
        finally { ResetTransfer(); }
    }
}
