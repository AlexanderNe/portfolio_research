namespace PortfolioTradingSystem.Api.Admin;

/// <summary>One point of the total-equity curve.</summary>
public sealed record EquityPointDto(DateTimeOffset Time, decimal Equity);

/// <summary>Payload for the total-equity chart.</summary>
public sealed record EquityDataDto(
    IReadOnlyList<EquityPointDto> Points,
    decimal InitialPool,
    decimal Realized,
    decimal Unrealized,
    decimal Equity);