using Korty.Core.Hid;

namespace Korty.Core.Discovery;

public enum ConnectionChangeKind { Arrived, Removed, Changed }

public sealed record ConnectionChange(ConnectionChangeKind Kind, ConnectionKind Device, string Description);

public sealed class TopologyChangedEventArgs : EventArgs
{
    public required DeviceTopology Topology { get; init; }
    public required IReadOnlyList<ConnectionChange> Changes { get; init; }
}

/// <summary>
/// Polls a read-only enumerator off the UI thread and raises events when the set of physical devices changes.
/// A device must be absent for <see cref="RemovalConfirmations"/> consecutive scans before it is reported as removed,
/// which filters the transient disappearances that happen while a keyboard re-enumerates during a USB/2.4 GHz switch.
/// </summary>
public sealed class ConnectionMonitor : IDisposable
{
    private readonly IHidEnumerator _enumerator;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DeviceTopology _stable = DeviceTopology.Empty;
    private readonly Dictionary<string, int> _missing = new();
    private readonly object _gate = new();

    public int RemovalConfirmations { get; init; } = 2;
    public DeviceTopology Current { get { lock (_gate) return _stable; } }
    public string? LastError { get; private set; }
    public event EventHandler<TopologyChangedEventArgs>? TopologyChanged;

    public ConnectionMonitor(IHidEnumerator enumerator, TimeSpan? interval = null)
    {
        _enumerator = enumerator;
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_interval);
            await PollOnceAsync().ConfigureAwait(false);
            try { while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) await PollOnceAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>One scan + diff. Public so tests (and a manual "refresh" button) can drive it deterministically.</summary>
    public Task<DeviceTopology> PollOnceAsync() => Task.Run(() =>
    {
        DeviceTopology scanned;
        try
        {
            scanned = DeviceClassifier.Classify(_enumerator.Enumerate());
            LastError = null;
        }
        catch (Exception ex)
        {
            // Enumeration failure must never crash the app or be mistaken for "device removed".
            LastError = ex.Message;
            return Current;
        }

        List<ConnectionChange> changes;
        DeviceTopology merged;
        lock (_gate)
        {
            (merged, changes) = Merge(_stable, scanned);
            _stable = merged;
        }
        if (changes.Count > 0)
            TopologyChanged?.Invoke(this, new TopologyChangedEventArgs { Topology = merged, Changes = changes });
        return merged;
    });

    private (DeviceTopology, List<ConnectionChange>) Merge(DeviceTopology old, DeviceTopology scanned)
    {
        var changes = new List<ConnectionChange>();
        var result = new List<PhysicalDevice>();
        var newByKey = scanned.Devices.ToDictionary(d => d.Key);
        var oldByKey = old.Devices.ToDictionary(d => d.Key);

        foreach (var d in scanned.Devices)
        {
            _missing.Remove(d.Key);
            result.Add(d);
            if (!oldByKey.TryGetValue(d.Key, out var prev))
                changes.Add(new(ConnectionChangeKind.Arrived, d.Kind, $"{d.Title} · {d.VidPid} · vendor-интерфейсов: {d.VendorInterfaceCount}"));
            else if (prev.Interfaces.Select(i => i.Info.Fingerprint).Order().SequenceEqual(d.Interfaces.Select(i => i.Info.Fingerprint).Order()) == false)
                changes.Add(new(ConnectionChangeKind.Changed, d.Kind, $"{d.Title}: изменился набор интерфейсов"));
        }
        foreach (var prev in old.Devices)
        {
            if (newByKey.ContainsKey(prev.Key)) continue;
            int n = _missing.GetValueOrDefault(prev.Key) + 1;
            if (n >= RemovalConfirmations)
            {
                _missing.Remove(prev.Key);
                changes.Add(new(ConnectionChangeKind.Removed, prev.Kind, $"{prev.Title} · {prev.VidPid} отключено"));
            }
            else { _missing[prev.Key] = n; result.Add(prev); }
        }
        return (new DeviceTopology(result.OrderBy(d => d.ProductId).ToList(), scanned.TotalHidInterfaces, scanned.IgnoredOtherVendorInterfaces), changes);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        _cts?.Dispose();
    }
}
