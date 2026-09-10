namespace PortfolioTradingSystem.Application.Engine;

/// <summary>Live per-instrument algo runtime metrics shown in the admin panel.</summary>
public sealed class InstrumentMetrics
{
    public const string StatusStopped = "stopped";
    public const string StatusWarmingUp = "warming up";
    public const string StatusStreaming = "running";
    public const string StatusError = "error";

    public Guid InstrumentId { get; init; }

    public string Ticker { get; init; } = string.Empty;

    /// <summary>stopped | warming up | running | error.</summary>
    public string Status { get; set; } = StatusStopped;

    public DateTimeOffset? LastRunTime { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? LastCandleTime { get; set; }

    public decimal? LastCandleClose { get; set; }

    public DateTimeOffset? LastProcessedBarTime { get; set; }

    public long BarsProcessed { get; set; }

    public bool WarmupDone { get; set; }

    public int WarmupBars { get; set; }

    public decimal? LatestClose { get; set; }

    public decimal? Roc { get; set; }

    public decimal? Atr { get; set; }

    public decimal Cash { get; set; }

    public decimal? Equity { get; set; }

    public int? PendingSignal { get; set; }

    /// <summary>flat | long | short.</summary>
    public string PositionState { get; set; } = "flat";

    public DateTimeOffset? PositionSince { get; set; }

    /// <summary>Open position unrealized PnL in RUB at the last price (null when flat).</summary>
    public decimal? PositionPnl { get; set; }

    /// <summary>Open position unrealized PnL as % of entry notional (null when flat).</summary>
    public decimal? PositionPnlPercent { get; set; }
}