using PortfolioTradingSystem.Domain.Enums;

namespace PortfolioTradingSystem.Domain.Models;

/// <summary>Audit log of a signal that was published to the Telegram channel.</summary>
public sealed class SignalLogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid InstrumentId { get; set; }

    public string Ticker { get; set; } = string.Empty;

    public SignalType Type { get; set; }

    public SignalDirection Direction { get; set; }

    public decimal Price { get; set; }

    public decimal? StopLoss { get; set; }

    public decimal? TakeProfit { get; set; }

    public decimal? ExitPrice { get; set; }

    public ExitReason? ExitReason { get; set; }

    /// <summary>For closing messages: PnL in percent (e.g. +2.35 or -1.12).</summary>
    public decimal? PnlPercent { get; set; }

    public DateTimeOffset Timestamp { get; set; }
}