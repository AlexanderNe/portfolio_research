namespace PortfolioTradingSystem.Application.Engine;

/// <summary>
/// Owns the set of running per-instrument engines. Applies the global controls
/// (stop all / resume all / reset) and re-syncs running engines with the
/// instrument table (active + running + has FIGI) on a schedule.
/// </summary>
public interface IEngineSupervisor
{
    IReadOnlyList<InstrumentEngine> Engines { get; }

    /// <summary>Live engine for the instrument, or null when it is not running.</summary>
    InstrumentEngine? GetEngine(Guid instrumentId);

    bool IsGloballyRunning { get; }

    /// <summary>Immediately re-sync engines with the current instrument table state.</summary>
    Task SyncInstrumentsAsync(CancellationToken ct);

    Task StopAllAsync(CancellationToken ct);

    Task ResumeAllAsync(CancellationToken ct);

    Task ResetAllAsync(CancellationToken ct);
}