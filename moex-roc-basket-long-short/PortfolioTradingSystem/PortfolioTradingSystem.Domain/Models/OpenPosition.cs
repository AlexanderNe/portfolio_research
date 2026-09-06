using PortfolioTradingSystem.Domain.Enums;

namespace PortfolioTradingSystem.Domain.Models;

public sealed class OpenPosition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid InstrumentId { get; set; }

    public string Ticker { get; set; } = string.Empty;

    public SignalDirection Direction { get; set; }

    public int Units { get; set; }

    public decimal EntryPrice { get; set; }

    public decimal StopLoss { get; set; }

    public decimal TakeProfit { get; set; }

    public decimal AtrAtEntry { get; set; }

    public decimal OpenCommission { get; set; }

    public DateTimeOffset EntryTime { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}