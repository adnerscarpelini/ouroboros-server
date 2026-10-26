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

    public EmailOutboxMetrics()
    {
        _failures = _meter.CreateCounter<long>("outbox.publish.failures", description: "Falhas de publicação confirmada.");
        _retries = _meter.CreateCounter<long>("outbox.publish.retries", description: "Reivindicações após uma tentativa anterior.");
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
    internal void Failure() => _failures.Add(1);
    internal void Retry() => _retries.Add(1);
    public void Dispose() => _meter.Dispose();
}
