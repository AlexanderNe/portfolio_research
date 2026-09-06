namespace PortfolioTradingSystem.Domain.Models;

/// <summary>Price bar. Time is the bar open time (converted to the exchange/Moscow tz).</summary>
public readonly record struct Candle(
    DateTimeOffset Time,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    bool IsComplete = true);