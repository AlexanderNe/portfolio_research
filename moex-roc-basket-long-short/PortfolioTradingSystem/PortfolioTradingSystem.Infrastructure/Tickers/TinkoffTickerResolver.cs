using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioTradingSystem.Application.Configuration;
using PortfolioTradingSystem.Application.Ports;
using PortfolioTradingSystem.Infrastructure.Tinkoff;
using Tinkoff.InvestApi.V1;

namespace PortfolioTradingSystem.Infrastructure.Tickers;

/// <summary>
/// Resolves a bare ticker to a single T-Invest share instrument via FindInstrument,
/// restricted to the configured class code (default TQBR — MOEX equities main board).
/// Returns an explicit error listing all candidates when the ticker is ambiguous;
/// never guesses.
/// </summary>
public sealed class TinkoffTickerResolver : ITickerResolver
{
    private readonly TinkoffConnection _connection;
    private readonly ILogger<TinkoffTickerResolver> _logger;
    private readonly string _classCode;

    public TinkoffTickerResolver(
        TinkoffConnection connection,
        IOptions<TinkoffOptions> options,
        ILogger<TinkoffTickerResolver> logger)
    {
        _connection = connection;
        _classCode = (options.Value.ResolveClassCode ?? string.Empty).Trim().ToUpperInvariant();
        _logger = logger;
    }

    public async Task<TickerResolution> ResolveAsync(string ticker, CancellationToken ct)
    {
string t = (ticker ?? string.Empty).Trim().ToUpperInvariant();
        if (t.Length == 0)
        {
            return TickerResolution.NotFound("Ticker is empty.");
        }

        if (_classCode.Length == 0)
        {
            _logger.LogDebug("Ticker resolution class code is not configured; matching any class");
        }

        FindInstrumentResponse response;
        try
        {
            response = await _connection.Instruments.FindInstrumentAsync(
                new FindInstrumentRequest { Query = t, InstrumentKind = InstrumentType.Share },
                new CallOptions(headers: _connection.Metadata, cancellationToken: ct)).ConfigureAwait(false);
        }
catch (RpcException ex)
        {
            _logger.LogError(ex, "FindInstrument RPC failed for ticker {Ticker}", t);
            return TickerResolution.NotFound("T-Invest lookup failed: " + ex.Status.Detail);
        }

var matches = response.Instruments
            .Where(i => i.InstrumentKind == InstrumentType.Share
                        && string.Equals(i.Ticker, t, StringComparison.OrdinalIgnoreCase)
                        && (_classCode.Length == 0
                            || string.Equals(i.ClassCode, _classCode, StringComparison.OrdinalIgnoreCase)))
            .Select(i => new ResolutionCandidate(
                i.Uid,
                i.Figi,
                i.Isin,
                i.Ticker,
                i.ClassCode,
                i.Name,
                i.Lot,
                i.ApiTradeAvailableFlag))
            .DistinctBy(c => c.Uid)
            .OrderBy(c => c.ClassCode)
            .ToList();

if (matches.Count == 1)
        {
            _logger.LogInformation(
                "Resolved ticker {Ticker} to uid={Uid} figi={Figi} class={ClassCode}",
                t, matches[0].Uid, matches[0].Figi, matches[0].ClassCode);
            return TickerResolution.Resolved(matches[0]);
        }

        if (matches.Count == 0)
        {
            string scope = _classCode.Length == 0 ? "any T-Invest class" : $"T-Invest class {_classCode}";
            _logger.LogInformation("No {Scope} share found for ticker {Ticker}", scope, t);
            return TickerResolution.NotFound($"No tradable share with ticker {t} found ({scope}).");
        }

        _logger.LogWarning(
            "Ambiguous ticker {Ticker}: {Count} candidates ({Codes})",
            t, matches.Count, string.Join(",", matches.Select(m => m.ClassCode)));
        return TickerResolution.Ambiguous(
            $"Ticker {t} matches {matches.Count} instruments; specify the exact instrument (class code) to disambiguate.",
            matches);
    }
}
