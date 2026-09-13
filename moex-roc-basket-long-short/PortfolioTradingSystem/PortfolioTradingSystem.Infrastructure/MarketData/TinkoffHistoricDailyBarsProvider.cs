using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PortfolioTradingSystem.Application.Ports;
using Candle = PortfolioTradingSystem.Domain.Models.Candle;
using PortfolioTradingSystem.Infrastructure.Tinkoff;
using Tinkoff.InvestApi.V1;

namespace PortfolioTradingSystem.Infrastructure.MarketData;

/// <summary>
/// Historical daily bars from the T-Bank GetCandles endpoint (CANDLE_INTERVAL_DAY).
/// The API allows a range of up to 6 years per request; a single 6-year window
/// covers the warm-up need (700 bars ~ 3 years), and instruments with shorter
/// trading history simply return less data.
/// </summary>
public sealed class TinkoffHistoricDailyBarsProvider : IHistoricDailyBarsProvider
{
    private readonly TinkoffConnection _connection;

    public TinkoffHistoricDailyBarsProvider(TinkoffConnection connection)
    {
        _connection = connection;
    }

    public async Task<IReadOnlyList<Candle>> GetDailyBarsAsync(string instrumentId, int maxBars, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(instrumentId))
        {
            return Array.Empty<Candle>();
        }

        DateTimeOffset to = TinkoffMappers.TodayMoscowStart();
        DateTimeOffset from = to.AddYears(-6);

        var response = await _connection.MarketData.GetCandlesAsync(
            new GetCandlesRequest
            {
                InstrumentId = instrumentId,
                Interval = CandleInterval.Day,
                From = from.ToTimestamp(),
                To = to.ToTimestamp(),
                Limit = 2400,
            },
            new CallOptions(headers: _connection.Metadata, cancellationToken: ct)).ConfigureAwait(false);

        var bars = response.Candles
            .Select(c => TinkoffMappers.ToCandle(c))
            .Where(c => MoscowDate(c.Time) < MoscowDate(DateTimeOffset.UtcNow))
            .OrderBy(c => c.Time)
            .ToList();

        if (bars.Count > maxBars)
        {
            bars = bars.Skip(bars.Count - maxBars).ToList();
        }

        return bars.ToList();
    }

    private static DateOnly MoscowDate(DateTimeOffset t) => Application.Engine.MoscowClock.ToMoscowDate(t);
}