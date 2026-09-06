namespace PortfolioTradingSystem.Application.Engine;

/// <summary>Thread-safe in-memory store of per-instrument runtime metrics.</summary>
public interface IMetricsStore
{
    void Initialize(Guid instrumentId, string ticker);

    void Remove(Guid instrumentId);

    void RemoveAll();

    InstrumentMetrics? Get(Guid instrumentId);

    IReadOnlyList<InstrumentMetrics> GetAll();

    /// <summary>Apply a mutation to the metrics of an instrument (no-op if not initialized).</summary>
    void Update(Guid instrumentId, Action<InstrumentMetrics> mutate);
}