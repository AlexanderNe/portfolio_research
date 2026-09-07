using PortfolioTradingSystem.Domain.Models;
using PortfolioTradingSystem.Domain.Strategy;
using Xunit;

namespace PortfolioTradingSystem.Tests;

internal static class TestHelpers
{
    internal static readonly DateTimeOffset T0 = new(2025, 1, 1, 10, 0, 0, TimeSpan.FromHours(3));

    internal static StrategyOptions Defaults() => new()
    {
        RocBars = 5,
        RocThreshold = 0.01m,
        AtrPeriod = 14,
        SlAtr = 2.0m,
        TpAtr = 3.0m,
        RiskPct = 10.0m,
        CommissionPct = 0.04m,
        PointRub = 1.0m,
        Leverage = 1.0m,
        InitialCapital = 100_000m,
        ShortsEnabled = true,
    };

    internal static MomentumEngineState New(StrategyOptions? options = null) =>
        new(options ?? Defaults(), "TST");

    /// <summary>Daily bar at day offset with open == close.</summary>
    internal static Candle Day(int day, decimal high, decimal low, decimal close) =>
        new(T0.AddDays(day), close, high, low, close, 0L);

    internal static IReadOnlyList<Candle> Closes(params decimal[] closes) =>
        closes.Select((c, i) => Day(i, c, c, c)).ToList();

    /// <summary>
    /// 6 daily bars whose true range is constant 9 (ATR stays exactly 9) and whose
    /// last close over first close produces a clear signal: +1 for longSignal=true,
    /// -1 for longSignal=false.
    /// </summary>
    internal static IReadOnlyList<Candle> Atr9Bars(bool longSignal)
    {
        return longSignal
            ? new[]
            {
                Day(0, 10, 1, 5), Day(1, 11, 2, 7), Day(2, 12, 3, 9),
                Day(3, 13, 4, 11), Day(4, 14, 5, 13), Day(5, 15, 6, 15),
            }
            : new[]
            {
                Day(0, 17, 8, 13), Day(1, 15, 6, 11), Day(2, 13, 4, 9),
                Day(3, 11, 2, 7), Day(4, 9, 0, 5), Day(5, 7, -2, 3),
            };
    }

    internal static MomentumEngineState Warmed(bool longSignal)
    {
        var st = New();
        st.WarmUp(Atr9Bars(longSignal));
        return st;
    }

    internal static void AssertAtrEqual(decimal actual, decimal expected, decimal tolerance = 1e-9m) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance, $"ATR {actual} != {expected} (+/- {tolerance})");
}