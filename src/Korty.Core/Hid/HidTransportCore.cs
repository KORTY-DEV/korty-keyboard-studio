using Korty.Core.Protocol;
using Korty.Core.Safety;

namespace Korty.Core.Hid;

/// <summary>
/// Thin OS abstraction used by <see cref="HidTransportCore"/>. All buffers INCLUDE the leading report-id byte, exactly as
/// the Windows HID API expects (WebHID passes the id separately and the data without it — the Windows buffer is id + data).
/// Implemented for Windows in Korty.Hid.Windows; faked in tests.
/// </summary>
public interface IHidDeviceIo : IDisposable
{
    /// <summary>HidD_SetFeature equivalent. Throws on failure.</summary>
    void SetFeature(string path, byte[] bufferWithReportId);
    /// <summary>HidD_GetFeature equivalent for report id 0. Returns null when the device does not answer (non-fatal).</summary>
    byte[]? GetFeature(string path, int lengthWithReportId);
    /// <summary>WriteFile equivalent for an output report. Throws on failure or short write.</summary>
    void WriteOutput(string path, byte[] bufferWithReportId);
    /// <summary>Waits up to <paramref name="timeoutMs"/> for an input report. Returns null on timeout.</summary>
    byte[]? ReadInput(string path, int lengthWithReportId, int timeoutMs);
    /// <summary>Discards queued, unread input reports so that a later read cannot return a stale ACK.</summary>
    void FlushInput(string path);
}

/// <summary>
/// Platform-independent transport: accepts only <see cref="ApprovedWrite"/> tokens, re-validates the essentials as defence
/// in depth, builds the Windows buffers (report id + data) and reproduces the reference's feature-read-back ("handshake")
/// and per-chunk ACK wait. The OS calls themselves live behind <see cref="IHidDeviceIo"/>.
/// </summary>
public sealed class HidTransportCore : IHidTransport, IDisposable
{
    private readonly IHidDeviceIo _io;
    public HidTransportCore(IHidDeviceIo io) { _io = io; }

    public Task<TransportResult> SendAsync(ApprovedWrite write, CancellationToken cancellationToken)
    {
        var t = write.Target; var p = write.Packet;
        if (t.IsSimulated) return Task.FromResult(TransportResult.Fail("simulated interface passed to real transport"));
        if (t.VendorId != 0x0C45) return Task.FromResult(TransportResult.Fail("refusing non-0C45 device"));
        int expected = p.Kind == ReportKind.Output ? t.OutputReportLength : t.FeatureReportLength;
        if (expected != p.WireLength) return Task.FromResult(TransportResult.Fail($"report length {p.WireLength} ≠ descriptor {expected}"));
        var buffer = p.ToWireBuffer();

        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (p.Kind == ReportKind.Feature)
                {
                    _io.SetFeature(t.Path, buffer);
                    if (p.HandshakeAfterMs is int wait)
                    {
                        if (wait > 0) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                        byte[]? reply = null;
                        try { reply = _io.GetFeature(t.Path, t.FeatureReportLength); }
                        catch (Exception) { /* the reference treats a failed read-back as non-critical */ }
                        return new TransportResult(true, buffer.Length, null, reply);
                    }
                    return new TransportResult(true, buffer.Length);
                }

                byte[]? ack = null;
                if (p.AckWaitMs > 0 && t.InputReportLength > 0) _io.FlushInput(t.Path);
                _io.WriteOutput(t.Path, buffer);
                if (p.AckWaitMs > 0 && t.InputReportLength > 0)
                {
                    try { ack = _io.ReadInput(t.Path, t.InputReportLength, p.AckWaitMs); }
                    catch (Exception) { /* a missing ACK is counted, not fatal (as in the reference) */ }
                }
                return new TransportResult(true, buffer.Length, null, ack);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return TransportResult.Fail(ex.Message); }
        }, cancellationToken);
    }

    public void Dispose() => _io.Dispose();
}
