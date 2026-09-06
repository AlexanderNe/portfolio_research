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
    Task<IReadOnlyList<Candle>> GetDailyBarsAsync(string figi, int maxBars, CancellationToken ct);
}

/// <summary>Publishes trading signals to the configured Telegram channel.</summary>
public interface ITelegramGateway
{
    Task SendMessageAsync(string text, CancellationToken ct);
}