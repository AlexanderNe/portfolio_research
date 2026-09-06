using PortfolioTradingSystem.Domain.Enums;

namespace PortfolioTradingSystem.Domain.Models;

public sealed class TradeLogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid InstrumentId { get; set; }

    public string Ticker { get; set; } = string.Empty;

    public SignalDirection Direction { get; set; }

    public int Units { get; set; }

    public decimal EntryPrice { get; set; }

    public decimal ExitPrice { get; set; }

    /// <summary>Net PnL in RUB after entry+exit commission.</summary>
    public decimal PnlRub { get; set; }

    /// <summary>PnL relative to the notional at entry, in percent.</summary>
    public decimal ReturnPercent { get; set; }

    public ExitReason ExitReason { get; set; }

    public decimal Commission { get; set; }

    public DateTimeOffset EntryTime { get; set; }

    public DateTimeOffset ExitTime { get; set; }
}