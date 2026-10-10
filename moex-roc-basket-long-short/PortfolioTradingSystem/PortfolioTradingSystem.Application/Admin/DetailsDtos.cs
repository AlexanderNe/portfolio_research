using PortfolioTradingSystem.Domain.Models;

namespace PortfolioTradingSystem.Application.Admin;

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

/// <summary>Payload for the per-instrument details page: price chart plus realized /
/// unrealized PnL, the pending signal and the most recent signals.</summary>
public sealed record InstrumentDetailsDto(
    string Ticker,
    string Name,
    IReadOnlyList<ChartCandleDto> Candles,
    IReadOnlyList<ChartTradeDto> Trades,
    ChartOpenPositionDto? OpenPosition,
    decimal RealizedPnlRub,
    decimal? UnrealizedPnlRub,
    decimal? UnrealizedPnlPercent,
    decimal TotalPnlRub,
    decimal TotalPnlPercent,
    int? PendingSignal,
    IReadOnlyList<SignalLogEntry> Signals);

/// <summary>One point of the (portfolio- or instrument-level) equity curve.</summary>
public sealed record EquityPointDto(DateTimeOffset Time, decimal Equity);

/// <summary>Payload for the total-equity chart.</summary>
public sealed record EquityDataDto(
    IReadOnlyList<EquityPointDto> Points,
    decimal InitialPool,
    decimal Realized,
    decimal Unrealized,
    decimal Equity);