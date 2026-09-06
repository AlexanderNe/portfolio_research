namespace PortfolioTradingSystem.Application.Ports;

public enum TickerResolutionStatus
{
    Resolved,
    NotFound,
    Ambiguous,
}

/// <summary>A single T-Invest instrument matched for a ticker during resolution.</summary>
public sealed record ResolutionCandidate(
    string Uid,
    string Figi,
    string? Isin,
    string Ticker,
    string ClassCode,
    string? Name,
    int Lot,
    bool ApiTradeAvailable);

/// <summary>Result of resolving a user-entered ticker against the T-Invest instruments.</summary>
public sealed record TickerResolution(
    TickerResolutionStatus Status,
    ResolutionCandidate? Candidate,
    string? Message,
    IReadOnlyList<ResolutionCandidate>? Candidates)
{
    public static TickerResolution Resolved(ResolutionCandidate c) =>
        new(TickerResolutionStatus.Resolved, c, null, null);

    public static TickerResolution NotFound(string message) =>
        new(TickerResolutionStatus.NotFound, null, message, null);

    public static TickerResolution Ambiguous(string message, IReadOnlyList<ResolutionCandidate> candidates) =>
        new(TickerResolutionStatus.Ambiguous, null, message, candidates);
}

/// <summary>
/// Resolves a bare ticker to a T-Invest instrument. Reports an explicit error when
/// the ticker is ambiguous (multiple matches) instead of guessing.
/// </summary>
public interface ITickerResolver
{
    Task<TickerResolution> ResolveAsync(string ticker, CancellationToken ct);
}