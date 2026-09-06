using System.Collections.Concurrent;

namespace PortfolioTradingSystem.Application.Engine;

public sealed class InMemoryMetricsStore : IMetricsStore
{
    private readonly ConcurrentDictionary<Guid, InstrumentMetrics> _items = new();
    private readonly object _gate = new();

    public void Initialize(Guid instrumentId, string ticker) =>
        _items.TryAdd(instrumentId, new InstrumentMetrics { InstrumentId = instrumentId, Ticker = ticker });

    public void Remove(Guid instrumentId) => _items.TryRemove(instrumentId, out _);

    public void RemoveAll() => _items.Clear();

    public InstrumentMetrics? Get(Guid instrumentId) => _items.TryGetValue(instrumentId, out var m) ? m : null;

    public IReadOnlyList<InstrumentMetrics> GetAll()
    {
        lock (_gate)
        {
            return _items.Values.ToList();
        }
    }

    public void Update(Guid instrumentId, Action<InstrumentMetrics> mutate)
    {
        if (!_items.TryGetValue(instrumentId, out var m))
        {
            return;
        }

        lock (_gate)
        {
            mutate(m);
        }
    }
}