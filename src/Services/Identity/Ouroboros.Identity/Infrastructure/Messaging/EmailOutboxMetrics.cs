using System.Diagnostics.Metrics;

namespace Ouroboros.Identity.Infrastructure.Messaging;

internal sealed class EmailOutboxMetrics : IDisposable
{
    private readonly Meter _meter = new("Ouroboros.Identity.Outbox", "1.0.0");
    private readonly Counter<long> _failures;
    private readonly Counter<long> _retries;
    private long _backlog;
    private double _oldestAge;
    private bool _hasSnapshot;
    private Dictionary<string, long>? _terminal;

    public EmailOutboxMetrics()
    {
        _failures = _meter.CreateCounter<long>("outbox.publish.failures", description: "Falhas de publicação confirmada.");
        _retries = _meter.CreateCounter<long>("outbox.publish.retries", description: "Reivindicações após uma tentativa anterior.");
        _meter.CreateObservableGauge("outbox.terminal", () => Volatile.Read(ref _terminal) is { } snapshot
            ? new[] { "Cancelled", "Expired", "Failed" }.Select(state => new Measurement<long>(snapshot.GetValueOrDefault(state), new KeyValuePair<string, object?>("state", state)))
            : Array.Empty<Measurement<long>>(), description: "Intenções terminais ainda retidas no banco.");
        _meter.CreateObservableGauge<long>("outbox.backlog", () => Volatile.Read(ref _hasSnapshot)
            ? [new Measurement<long>(Volatile.Read(ref _backlog))] : Array.Empty<Measurement<long>>(), description: "Intenções pendentes ainda válidas.");
        _meter.CreateObservableGauge<double>("outbox.oldest.age", () => Volatile.Read(ref _hasSnapshot)
            ? [new Measurement<double>(Volatile.Read(ref _oldestAge))] : Array.Empty<Measurement<double>>(), "s", "Idade da intenção pendente mais antiga.");
    }

    internal void Snapshot(long backlog, double age)
    {
        Volatile.Write(ref _backlog, backlog);
        Volatile.Write(ref _oldestAge, age);
        Volatile.Write(ref _hasSnapshot, true);
    }
    internal void TerminalSnapshot(Dictionary<string, long> terminal) => Volatile.Write(ref _terminal, terminal);
    internal void Failure(string? code = null) => _failures.Add(1, new KeyValuePair<string, object?>("code", Code(code)));
    internal void Retry(string? code = null) => _retries.Add(1, new KeyValuePair<string, object?>("code", Code(code)));
    private static string Code(string? code) => code is "invalid-contract" or "configuration-suspended" or "broker-rejected" or "publish-unconfirmed" ? code : "unknown";
    public void Dispose() => _meter.Dispose();
}
