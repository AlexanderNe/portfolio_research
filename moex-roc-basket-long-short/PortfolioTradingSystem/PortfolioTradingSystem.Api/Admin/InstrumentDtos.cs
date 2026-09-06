using PortfolioTradingSystem.Application.Engine;
using PortfolioTradingSystem.Domain.Models;

namespace PortfolioTradingSystem.Api.Admin;

/// <summary>Create/update payload for an instrument.</summary>
public sealed class InstrumentUpsertDto
{
    public string Ticker { get; set; } = string.Empty;

    public string? Isin { get; set; }

    public string? Figi { get; set; }

    public string? Uid { get; set; }

    public string? ClassCode { get; set; }

    public string? Name { get; set; }

    public int? LotSize { get; set; }

    public string? FutureTicker { get; set; }

    public string? FutureFigi { get; set; }

    public string? FutureClassCode { get; set; }

    /// <summary>"running" or "paused".</summary>
    public string? ProcessingStatus { get; set; }
}

/// <summary>Instrument together with its live metrics, as returned by the admin API.</summary>
public sealed record InstrumentView(Instrument Instrument, InstrumentMetrics? Metrics);