using PortfolioTradingSystem.Application.Reporting;

namespace PortfolioTradingSystem.Tests;

/// <summary>
/// The pooled end-of-day equity behind /api/equity-data: pool + realized PnL +
/// open positions marked to each day's close.
/// </summary>
public class EquityCurveTests
{
    private const decimal Pool = 100_000m;
    private static readonly TimeSpan Msk = TimeSpan.FromHours(3);

    // Monday 2026-01-12 + n days, at the given Moscow time.
    private static DateTimeOffset Day(int n, int hour = 10, int minute = 0) =>
        new DateTimeOffset(2026, 1, 12, hour, minute, 0, Msk).AddDays(n);

    private static Candle Bar(int n, decimal close) => new(Day(n), close, close, close, close, 0L);

    private static TradeLogEntry Trade(
        string ticker, SignalDirection dir, int units, decimal entry, DateTimeOffset entryTime,
        decimal exit, DateTimeOffset exitTime) => new()
    {
        Ticker = ticker,
        Direction = dir,
        Units = units,
        EntryPrice = entry,
        ExitPrice = exit,
        EntryTime = entryTime,
        ExitTime = exitTime,
        PnlRub = (exit - entry) * units * (int)dir,
    };

    private static List<decimal> Build(
        IReadOnlyList<TradeLogEntry> trades,
        IReadOnlyDictionary<string, OpenPosition> open,
        IReadOnlyDictionary<string, IReadOnlyList<Candle>> bars) =>
        EquityCurve.Build(Pool, trades, open, bars, pointRub: 1m, commissionRate: 0m)
            .Select(p => p.Equity)
            .ToList();

    [Fact]
    public void TradeIsValuedAtItsExitPriceOnTheExitDay()
    {
        // LONG 100 @ 100 on day 0, stopped out intraday at 90 on day 2 while day 2
        // CLOSED at 95. The old curve marked the trade to 95 on its exit day and
        // realized it only the next day: a one-day loss understated by 500.
        var bars = new Dictionary<string, IReadOnlyList<Candle>>
        {
            ["AAA"] = new[] { Bar(0, 100m), Bar(1, 97m), Bar(2, 95m), Bar(3, 96m) },
        };
        var trades = new[] { Trade("AAA", SignalDirection.Long, 100, 100m, Day(0), 90m, Day(2, 12)) };

        var equity = Build(trades, new Dictionary<string, OpenPosition>(), bars);

        Assert.Equal(new[] { Pool, Pool - 300m, Pool - 1_000m, Pool - 1_000m }, equity);
    }

    [Fact]
    public void FlipDayCountsTheClosedLegAtTheOpenAndTheNewLegAtTheClose()
    {
        // LONG 100 @ 95 flipped at day 1's open (100) into SHORT 100 @ 100; day 1
        // closes at 104. Real end-of-day equity: +500 realized, -400 on the short.
        var bars = new Dictionary<string, IReadOnlyList<Candle>>
        {
            ["AAA"] = new[] { Bar(0, 96m), Bar(1, 104m) },
        };
        var trades = new[] { Trade("AAA", SignalDirection.Long, 100, 95m, Day(0), 100m, Day(1)) };
        var open = new Dictionary<string, OpenPosition>
        {
            ["AAA"] = new() { Ticker = "AAA", Direction = SignalDirection.Short, Units = 100, EntryPrice = 100m, EntryTime = Day(1) },
        };

        var equity = Build(trades, open, bars);

        Assert.Equal(new[] { Pool + 100m, Pool + 500m - 400m }, equity);
    }

    [Fact]
    public void DayWithoutABarKeepsThePositionAtItsLastClose()
    {
        // BBB has no bar on day 1 (thin name, paused instrument whose history ends
        // earlier, ...). Its open LONG used to vanish from that day's equity.
        var bars = new Dictionary<string, IReadOnlyList<Candle>>
        {
            ["AAA"] = new[] { Bar(0, 10m), Bar(1, 10m), Bar(2, 10m) },
            ["BBB"] = new[] { Bar(0, 55m), Bar(2, 60m) },
        };
        var open = new Dictionary<string, OpenPosition>
        {
            ["BBB"] = new() { Ticker = "BBB", Direction = SignalDirection.Long, Units = 10, EntryPrice = 50m, EntryTime = Day(0) },
        };

        var equity = Build(Array.Empty<TradeLogEntry>(), open, bars);

        Assert.Equal(new[] { Pool + 50m, Pool + 50m, Pool + 100m }, equity);
    }
}
