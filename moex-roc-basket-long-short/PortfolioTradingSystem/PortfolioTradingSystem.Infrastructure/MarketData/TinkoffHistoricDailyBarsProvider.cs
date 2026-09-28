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

        // Weekend bars are dropped for the same reason the engine ignores weekend
        // candles: the research series is Monday-Friday only, so a weekend bar in
        // the warm-up would shift the ROC window and feed ATR a day the model
        // never saw.
        var bars = response.Candles
            .Select(c => TinkoffMappers.ToCandle(c))
            .Where(c => MoscowDate(c.Time) < MoscowDate(DateTimeOffset.UtcNow)
                        && IsWeekday(MoscowDate(c.Time)))
            .OrderBy(c => c.Time)
            .ToList();

        if (bars.Count > maxBars)
        {
            bars = bars.Skip(bars.Count - maxBars).ToList();
        }

        return bars.ToList();
    }

    public async Task<Candle?> GetCurrentDayBarAsync(string instrumentId, CancellationToken ct)
    {
        DateOnly today = MoscowDate(DateTimeOffset.UtcNow);
        if (string.IsNullOrWhiteSpace(instrumentId) || !IsWeekday(today))
        {
            return null;
        }

        DateTimeOffset from = TinkoffMappers.TodayMoscowStart();
        var response = await _connection.MarketData.GetCandlesAsync(
            new GetCandlesRequest
            {
                InstrumentId = instrumentId,
                Interval = CandleInterval.Day,
                From = from.ToTimestamp(),
                To = from.AddDays(2).ToTimestamp(),
                Limit = 10,
            },
            new CallOptions(headers: _connection.Metadata, cancellationToken: ct)).ConfigureAwait(false);

        return response.Candles
            .Select(c => TinkoffMappers.ToCandle(c))
            .Where(c => MoscowDate(c.Time) == today)
            .OrderBy(c => c.Time)
            .LastOrDefault();
    }

    private static DateOnly MoscowDate(DateTimeOffset t) => Application.Engine.MoscowClock.ToMoscowDate(t);

    private static bool IsWeekday(DateOnly day) => Application.Engine.MoscowClock.IsWeekday(day);
}