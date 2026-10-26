using System.Diagnostics.Metrics;

namespace Ouroboros.Notifications.Infrastructure;

internal sealed class InboxMetrics : IDisposable
{
    private readonly Meter _meter = new("Ouroboros.Notifications.Inbox", "1.0.0");
    private readonly Counter<long> _accepted;
    private readonly Counter<long> _attempts;
    private Dictionary<string, long>? _snapshot;

    public InboxMetrics()
    {
        _accepted = _meter.CreateCounter<long>("inbox.acceptances");
        _attempts = _meter.CreateCounter<long>("inbox.delivery.attempts");
        _meter.CreateObservableGauge("inbox.failure.backlog", () => Volatile.Read(ref _snapshot) is { } snapshot
            ? [new Measurement<long>(snapshot.GetValueOrDefault("failures"))] : Array.Empty<Measurement<long>>());
        _meter.CreateObservableGauge("inbox.terminal", () => Volatile.Read(ref _snapshot) is { } snapshot
            ? new[] { "Expired", "Cancelled", "Failed" }.Select(state => new Measurement<long>(snapshot.GetValueOrDefault(state), new KeyValuePair<string, object?>("state", state)))
            : Array.Empty<Measurement<long>>());
    }

    internal void Accepted(Persistence.AcceptanceResult result) => _accepted.Add(1, new KeyValuePair<string, object?>("result", result.ToString()));
    internal void Attempt(string outcome) => _attempts.Add(1, new KeyValuePair<string, object?>("outcome",
        outcome is "delivered" or "transient" or "permanent" or "exhausted" or "expired" or "suspended" ? outcome : "unknown"));
    internal void Snapshot(Dictionary<string, long> snapshot) => Volatile.Write(ref _snapshot, snapshot);
    public void Dispose() => _meter.Dispose();
}
