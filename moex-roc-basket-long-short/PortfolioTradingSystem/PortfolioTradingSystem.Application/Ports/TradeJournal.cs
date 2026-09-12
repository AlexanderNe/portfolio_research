using PortfolioTradingSystem.Domain.Models;

namespace PortfolioTradingSystem.Application.Ports;

/// <summary>
/// Writes that must never be observable half-applied.
///
/// Opening and closing a position each touch several tables. Done as separate
/// single-statement repository calls, a crash between them leaves the engine
/// unable to reconstruct itself: a close that wrote the trade log but not the
/// position delete comes back as "realized PnL already counted AND position
/// still open", which double-counts the trade and resurrects a position that
/// no longer exists.
/// </summary>
public interface ITradeJournal
{
    /// <summary>Persist the new position and its signal-log entry in one transaction.</summary>
    Task RecordOpenAsync(OpenPosition position, SignalLogEntry signal, CancellationToken ct);

    /// <summary>Persist the closed trade and its signal-log entry, and drop the open position, in one transaction.</summary>
    Task RecordCloseAsync(Guid instrumentId, TradeLogEntry trade, SignalLogEntry signal, CancellationToken ct);
}
