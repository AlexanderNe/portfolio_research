using PortfolioTradingSystem.Domain.Enums;

namespace PortfolioTradingSystem.Domain.Models;

public sealed class Instrument
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Ticker { get; set; } = string.Empty;

    public string? Isin { get; set; }

    public string? Figi { get; set; }

    /// <summary>T-Bank instrument UID (primary identifier within T-Invest; FIGI is a fallback).</summary>
    public string? Uid { get; set; }

    public string? ClassCode { get; set; }

    public string? Name { get; set; }

    public int LotSize { get; set; } = 1;

    /// <summary>Optional futures contract used to execute SHORT positions (per research, short through stock futures).</summary>
    public string? FutureTicker { get; set; }

    public string? FutureFigi { get; set; }

    public string? FutureClassCode { get; set; }

    public ProcessingStatus ProcessingStatus { get; set; } = ProcessingStatus.Paused;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}