namespace PortfolioTradingSystem.Api.Admin;

/// <summary>
/// One daily bar for the chart. A placeholder candle carries no price data
/// (all fields equal the neighbouring close, volume 0) but still gives a signal
/// drawn on a day with no data an x-slot to anchor to.
/// </summary>
public sealed record ChartCandleDto(
    DateTimeOffset Time,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    bool IsPlaceholder = false);

/// <summary>One completed position (entry → exit) for the chart.</summary>
public sealed record ChartTradeDto(
    string Direction,
    DateTimeOffset EntryTime,
    decimal EntryPrice,
    DateTimeOffset ExitTime,
    decimal ExitPrice,
    string Reason,
    decimal? PnlPercent);

/// <summary>Currently open position (drawn with SL / TP / entry lines).</summary>
public sealed record ChartOpenPositionDto(
    string Direction,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TakeProfit,
    int Units,
    DateTimeOffset EntryTime);

/// <summary>Payload for the per-instrument price chart.</summary>
public sealed record ChartDataDto(
    string Ticker,
    string Name,
    IReadOnlyList<ChartCandleDto> Candles,
    IReadOnlyList<ChartTradeDto> Trades,
    ChartOpenPositionDto? OpenPosition);

/// <summary>One point of the total-equity curve.</summary>
public sealed record EquityPointDto(DateTimeOffset Time, decimal Equity);

/// <summary>Payload for the total-equity chart.</summary>
public sealed record EquityDataDto(
    IReadOnlyList<EquityPointDto> Points,
    decimal InitialPool,
    decimal Realized,
    decimal Unrealized,
    decimal Equity);