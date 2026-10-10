using Korty.Core.Safety;

namespace Korty.Core.Hid;

/// <summary>Read-only enumeration. Implementations MUST NOT open devices with write access or send any report.</summary>
public interface IHidEnumerator
{
    IReadOnlyList<HidInterfaceInfo> Enumerate();
}

public enum ReportKind { Output, Feature }

public sealed record TransportResult(bool Success, int BytesWritten, string? Error = null, byte[]? Response = null)
{
    public static TransportResult Fail(string error) => new(false, 0, error);
}

/// <summary>
/// The only way to put bytes on a device. Accepts <see cref="ApprovedWrite"/> exclusively; instances of that type can
/// only be created by <see cref="WriteGate"/>, so UI / application code cannot bypass safety checks.
/// </summary>
public interface IHidTransport
{
    Task<TransportResult> SendAsync(ApprovedWrite write, CancellationToken cancellationToken);
}
