using Google.Protobuf.WellKnownTypes;
using PortfolioTradingSystem.Application.Engine;
using PortfolioTradingSystem.Domain.Models;
using TCandle = Tinkoff.InvestApi.V1.Candle;
using THistoricCandle = Tinkoff.InvestApi.V1.HistoricCandle;
using TQuotation = Tinkoff.InvestApi.V1.Quotation;

namespace PortfolioTradingSystem.Infrastructure.Tinkoff;

internal static class TinkoffMappers
{
    /// <summary>Time used as the daily-bar cutoff: the current calendar day in Moscow.</summary>
    public static readonly DateTimeOffset TodayMoscowStart =
        new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero).ToOffset(MoscowClock.Offset);

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