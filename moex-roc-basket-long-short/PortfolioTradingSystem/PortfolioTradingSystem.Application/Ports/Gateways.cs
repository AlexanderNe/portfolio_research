using PortfolioTradingSystem.Domain.Models;

namespace PortfolioTradingSystem.Application.Ports;

/// <summary>
/// Real-time 1-minute candle feed for one instrument (subscription model).
/// The stream stays alive until the supplied token is cancelled. All callers share a
/// single underlying connection where possible; the gateway reconnects transparently,
/// so an enumerable completes only when unsubscribed or cancelled — callers do NOT
/// need their own reconnection logic.
/// </summary>
public interface IMarketDataGateway
{
    IAsyncEnumerable<Candle> SubscribeAsync(string instrumentId, CancellationToken ct);
}

/// <summary>Historical daily bars from the T-Bank API (warm-up history).</summary>
public interface IHistoricDailyBarsProvider
{
    /// <summary>Returns up to <paramref name="maxBars"/> completed daily bars, ascending, excluding the current day.</summary>
    Task<IReadOnlyList<Candle>> GetDailyBarsAsync(string instrumentId, int maxBars, CancellationToken ct);

    /// <summary>
    /// Returns the in-progress daily bar for the current Moscow day, or null when
    /// the API has none (non-trading day, market not opened yet). Read-only — used
    /// by the admin chart so a signal logged today still anchors to its own day
    /// even when the minute feed has not delivered a candle yet (e.g. right after
    /// a restart mid-session). Never feeds engine state.
    /// </summary>
    Task<Candle?> GetCurrentDayBarAsync(string instrumentId, CancellationToken ct);
}

/// <summary>
/// Publishes trading signals to the configured Telegram channel. Send failures
/// must be swallowed so the caller never loses signal/trade records because a
/// notification could not be delivered.
/// </summary>
public interface ITelegramGateway
{
    Task SendMessageAsync(string text, CancellationToken ct);
}