using PortfolioTradingSystem.Domain.Models;

namespace PortfolioTradingSystem.Application.Ports;

/// <summary>
/// Real-time 1-minute candle feed for one instrument. The sequence completes
/// when the underlying stream ends or is cancelled; callers are responsible for
/// reconnection (the implementing gateway raises transport errors as exceptions).
/// </summary>
public interface IMarketDataGateway
{
    IAsyncEnumerable<Candle> StreamOneMinuteCandlesAsync(string figi, CancellationToken ct);
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