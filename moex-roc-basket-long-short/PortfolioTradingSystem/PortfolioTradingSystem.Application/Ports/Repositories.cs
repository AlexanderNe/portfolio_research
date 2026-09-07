using PortfolioTradingSystem.Domain.Enums;
using PortfolioTradingSystem.Domain.Models;

namespace PortfolioTradingSystem.Application.Ports;

/// <summary>Persistence for the list of trading instruments (stocks).</summary>
public interface IInstrumentRepository
{
    Task<IReadOnlyList<Instrument>> GetAllAsync(CancellationToken ct);

    /// <summary>Instruments with running processing status.</summary>
    Task<IReadOnlyList<Instrument>> GetActiveAsync(CancellationToken ct);

    Task<Instrument?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<Instrument?> FindByTickerAsync(string ticker, CancellationToken ct);

    Task AddAsync(Instrument instrument, CancellationToken ct);

    Task UpdateAsync(Instrument instrument, CancellationToken ct);

    /// <summary>Sets the processing status for all instruments (global stop/resume).</summary>
    Task SetProcessingStatusAsync(ProcessingStatus status, CancellationToken ct);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}

/// <summary>Persistence for the single open position of an instrument.</summary>
public interface IPositionRepository
{
    Task<OpenPosition?> GetOpenAsync(Guid instrumentId, CancellationToken ct);

    Task SaveAsync(OpenPosition position, CancellationToken ct);

    Task DeleteAsync(Guid instrumentId, CancellationToken ct);
}

/// <summary>Persistence for closed-trade history (audit log).</summary>
public interface ITradeLogRepository
{
    Task AddAsync(TradeLogEntry entry, CancellationToken ct);

    Task<IReadOnlyList<TradeLogEntry>> GetByInstrumentAsync(Guid instrumentId, int limit, CancellationToken ct);

    /// <summary>All closed trades for the instrument, ordered by exit time ascending (chart markers).</summary>
    Task<IReadOnlyList<TradeLogEntry>> GetAllByInstrumentAsync(Guid instrumentId, CancellationToken ct);

    /// <summary>Sum of net PnL of all closed trades for the instrument (used to restore engine cash).</summary>
    Task<decimal> SumRealizedPnlAsync(Guid instrumentId, CancellationToken ct);
}

/// <summary>One page of signal-log entries, newest first.</summary>
public sealed record SignalLogPage(int Total, int Page, int PageSize, IReadOnlyList<SignalLogEntry> Items);

/// <summary>Persistence for signals published to Telegram (audit log).</summary>
public interface ISignalLogRepository
{
    Task AddAsync(SignalLogEntry entry, CancellationToken ct);

    Task<SignalLogEntry?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Most recent opened-position signal for the instrument (used to resend the active signal).</summary>
    Task<SignalLogEntry?> GetLatestOpenedAsync(Guid instrumentId, CancellationToken ct);

    Task<IReadOnlyList<SignalLogEntry>> GetByInstrumentAsync(Guid instrumentId, int limit, CancellationToken ct);

    /// <summary>Page of signals filtered by ticker (exact match, case-insensitive), newest first.</summary>
    Task<SignalLogPage> GetPageAsync(string? ticker, int page, int pageSize, CancellationToken ct);

    /// <summary>Distinct tickers present in the signal log, ascending.</summary>
    Task<IReadOnlyList<string>> GetTickersAsync(CancellationToken ct);
}