namespace PortfolioTradingSystem.Domain.Strategy;

/// <summary>
/// roc_momentum strategy parameters. Defaults mirror the validated research
/// configuration (TECHNICAL.md: n=5, thr=0.01, SL 2*ATR, TP 3*ATR, risk 10%,
/// commission 0.04% per side, leverage 1, initial capital 100k).
/// The research package is the single source of truth — do not re-derive here.
/// </summary>
public sealed class StrategyOptions
{
    /// <summary>ROC lookback in trading days (research: n=5).</summary>
    public int RocBars { get; set; } = 5;

    /// <summary>ROC threshold; |roc| &gt; threshold produces a signal (research: 0.01).</summary>
    public decimal RocThreshold { get; set; } = 0.01m;

    /// <summary>True-range EWM smoothing period for ATR (research: 14).</summary>
    public int AtrPeriod { get; set; } = 14;

    /// <summary>Stop-loss as a multiple of ATR (research: 2.0).</summary>
    public decimal SlAtr { get; set; } = 2.0m;

    /// <summary>Take-profit as a multiple of ATR (research: 3.0).</summary>
    public decimal TpAtr { get; set; } = 3.0m;

    /// <summary>Risk per position, % of cash (research: 10).</summary>
    public decimal RiskPct { get; set; } = 10.0m;

    /// <summary>Commission per trade side, % of turnover (research: 0.04).</summary>
    public decimal CommissionPct { get; set; } = 0.04m;

    /// <summary>RUB value of one price point (research: 1.0).</summary>
    public decimal PointRub { get; set; } = 1.0m;

    /// <summary>Margin leverage cap; 1 = no borrowed money (research: 1.0).</summary>
    public decimal Leverage { get; set; } = 1.0m;

    /// <summary>Initial capital used for position sizing (research: 100000).</summary>
    public decimal InitialCapital { get; set; } = 100_000m;

    /// <summary>
    /// When false, short signals are suppressed (research long-only mode).
    /// Shorts are executed through stock futures per the research.
    /// </summary>
    public bool ShortsEnabled { get; set; } = true;

    /// <summary>
    /// How long after the session open an entry may still be filled at that open.
    /// The research enters at the session open; a fill quoted hours later is not
    /// obtainable, so an engine that only joins mid-session waits for the next one.
    /// </summary>
    public int EntryWindowMinutes { get; set; } = 15;
}