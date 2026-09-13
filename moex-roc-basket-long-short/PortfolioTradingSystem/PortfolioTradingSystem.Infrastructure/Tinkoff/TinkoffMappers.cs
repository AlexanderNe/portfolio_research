using Google.Protobuf.WellKnownTypes;
using PortfolioTradingSystem.Application.Engine;
using PortfolioTradingSystem.Domain.Models;
using TCandle = Tinkoff.InvestApi.V1.Candle;
using THistoricCandle = Tinkoff.InvestApi.V1.HistoricCandle;
using TQuotation = Tinkoff.InvestApi.V1.Quotation;

namespace PortfolioTradingSystem.Infrastructure.Tinkoff;

internal static class TinkoffMappers
{
    /// <summary>
    /// Midnight of the current Moscow day. Must be evaluated per call: as a static
    /// readonly field it froze at process start, and this service is expected to
    /// run for months, so a warm-up after a few weeks of uptime asked T-Bank for
    /// history up to a date that was weeks stale.
    /// </summary>
    public static DateTimeOffset TodayMoscowStart()
    {
        DateTimeOffset moscowNow = DateTimeOffset.UtcNow.ToOffset(MoscowClock.Offset);
        return new DateTimeOffset(moscowNow.Date, MoscowClock.Offset);
    }

    public static decimal ToDecimal(TQuotation q) => q.Units + q.Nano / 1_000_000_000m;

    public static Candle ToCandle(TCandle c) => new(
        Time: c.Time.ToDateTimeOffset(),
        Open: ToDecimal(c.Open),
        High: ToDecimal(c.High),
        Low: ToDecimal(c.Low),
        Close: ToDecimal(c.Close),
        Volume: c.Volume);

    public static Candle ToCandle(THistoricCandle c) => new(
        Time: c.Time.ToDateTimeOffset(),
        Open: ToDecimal(c.Open),
        High: ToDecimal(c.High),
        Low: ToDecimal(c.Low),
        Close: ToDecimal(c.Close),
        Volume: c.Volume);

    public static bool IsTodayOrLater(DateTimeOffset time) =>
        MoscowClock.ToMoscowDate(time) >= MoscowClock.ToMoscowDate(DateTimeOffset.UtcNow);
}