using PortfolioTradingSystem.Domain.Enums;
using PortfolioTradingSystem.Domain.Models;
using Xunit;
using static PortfolioTradingSystem.Tests.TestHelpers;

namespace PortfolioTradingSystem.Tests;

/// <summary>
/// Live wiring of the engine: session aggregation, when an entry may be advised, and
/// what survives a restart. These paths held the branch's headline defect and had no
/// coverage at all.
/// </summary>
public class InstrumentEngineTests
{
    private static readonly TimeSpan Msk = TimeSpan.FromHours(3);

    private static Candle Minute(int day, int hour, int minute, decimal open, decimal high, decimal low, decimal close) =>
        new(new DateTimeOffset(2026, 1, day, hour, minute, 0, Msk), open, high, low, close, 100L);

    [Fact]
    public async Task DoesNotReEnterInTheSameSessionAfterAnExit()
    {
        await using var h = new EngineHarness(Atr9Bars(longSignal: true));
        await h.StartAsync();

        // Monday 10:00 - session open observed, signal is +1: enter at 100, SL 82.
        await h.FeedAsync(Minute(12, 10, 0, 100m, 101m, 99m, 100m));
        Assert.Single(h.Journal.Opens);
        Assert.Equal(100m, h.Journal.Opens[0].EntryPrice);

        // Tuesday 10:00 - new session, the position is stopped out intraday.
        await h.FeedAsync(Minute(13, 10, 0, 99m, 99m, 81m, 82m));
        Assert.Single(h.Journal.Closes);
        Assert.Equal(ExitReason.StopLoss, h.Journal.Closes[0].ExitReason);

        // Same session, still signalling long. The old engine re-entered here at the
        // session open (99) - a price that was gone by the time the stop was hit.
        await h.FeedAsync(
            Minute(13, 10, 1, 82m, 84m, 82m, 84m),
            Minute(13, 15, 0, 84m, 85m, 83m, 85m));

        Assert.Single(h.Journal.Opens);
        Assert.Null(h.Positions.Current);
    }

    [Fact]
    public async Task DoesNotEnterWhenTheSessionOpenWasNotObserved()
    {
        await using var h = new EngineHarness(Atr9Bars(longSignal: true));
        await h.StartAsync();

        // The engine joins at 14:00: _sessionBar.Open is simply the first candle it
        // saw, and advising a fill at it would be advising a price hours old.
        await h.FeedAsync(
            Minute(12, 14, 0, 100m, 101m, 99m, 100m),
            Minute(12, 14, 1, 100m, 102m, 100m, 101m));
        Assert.Empty(h.Journal.Opens);

        // Next session, observed from its open: entry is advised again.
        await h.FeedAsync(Minute(13, 10, 0, 100m, 101m, 99m, 100m));
        Assert.Single(h.Journal.Opens);
        Assert.Equal(100m, h.Journal.Opens[0].EntryPrice);
    }

    [Fact]
    public async Task EntryIsStillAdvisedWhenTheEngineStartsInsideTheEntryWindow()
    {
        await using var h = new EngineHarness(Atr9Bars(longSignal: true));
        await h.StartAsync();

        await h.FeedAsync(Minute(12, 10, 7, 100m, 101m, 99m, 100m));

        Assert.Single(h.Journal.Opens);
    }

    [Fact]
    public async Task PartiallyObservedSessionDoesNotMoveAtr()
    {
        await using var h = new EngineHarness(Atr9Bars(longSignal: true));
        await h.StartAsync();
        Assert.Equal(9m, h.Engine.GetSnapshot(100m)!.Atr);

        // Joined at 14:00, so Monday's bar is fabricated from 14:00 onwards. Tuesday's
        // first candle finalizes it; a range the engine never saw must not move ATR.
        await h.FeedAsync(
            Minute(12, 14, 0, 100m, 130m, 70m, 100m),
            Minute(13, 10, 0, 100m, 101m, 99m, 100m));

        Assert.Equal(9m, h.Engine.GetSnapshot(100m)!.Atr);
    }

    [Fact]
    public async Task RestartRestoresPositionAndTheCashItConsumed()
    {
        var position = new OpenPosition
        {
            Ticker = "TST",
            Direction = SignalDirection.Long,
            Units = 500,
            EntryPrice = 100m,
            StopLoss = 82m,
            TakeProfit = 127m,
            AtrAtEntry = 9m,
            OpenCommission = 20m,
            EntryTime = new DateTimeOffset(2026, 1, 9, 10, 0, 0, Msk),
        };

        await using var h = new EngineHarness(Atr9Bars(longSignal: true));
        h.Positions.Current = position;
        h.TradeLogs.Realized = 1_234m;
        await h.StartAsync();

        var snapshot = h.Engine.GetSnapshot(110m)!;
        Assert.Equal(SignalDirection.Long, snapshot.PositionDirection);
        Assert.Equal(500, snapshot.PositionUnits);
        // cash = capital + realized - (notional + open commission)
        Assert.Equal(100_000m + 1_234m - (500m * 100m) - 20m, snapshot.Cash);
        Assert.Equal(snapshot.Cash + 500m * 110m, snapshot.Equity);
    }

    [Fact]
    public async Task OppositeSignalAtSessionOpenFlipsTheOpenPosition()
    {
        // Restored LONG position; warm-up signals SHORT (-1). The engine joins at
        // the Monday open and must close the LONG at that open (signal_reversal)
        // and re-enter SHORT at the same open - the research flip.
        var position = new OpenPosition
        {
            Ticker = "TST",
            Direction = SignalDirection.Long,
            Units = 500,
            EntryPrice = 100m,
            StopLoss = 82m,
            TakeProfit = 127m,
            AtrAtEntry = 9m,
            OpenCommission = 20m,
            EntryTime = new DateTimeOffset(2026, 1, 9, 10, 0, 0, Msk),
        };

        await using var h = new EngineHarness(Atr9Bars(longSignal: false));
        h.Positions.Current = position;
        h.TradeLogs.Realized = 1_234m;
        await h.StartAsync();

        await h.FeedAsync(Minute(12, 10, 0, 100m, 101m, 99m, 100m));

        var close = Assert.Single(h.Journal.Closes);
        Assert.Equal(ExitReason.SignalReversal, close.ExitReason);
        Assert.Equal(100m, close.ExitPrice);

        var open = Assert.Single(h.Journal.Opens);
        Assert.Equal(SignalDirection.Short, open.Direction);
        Assert.Equal(100m, open.EntryPrice);

        var current = h.Positions.Current;
        Assert.NotNull(current);
        Assert.Equal(SignalDirection.Short, current.Direction);
        Assert.Equal(100m, current.EntryPrice);
    }

    [Fact]
    public async Task ResentOpeningCandleDoesNotFlipTwiceOrStopTheFlippedLeg()
    {
        var position = new OpenPosition
        {
            Ticker = "TST",
            Direction = SignalDirection.Long,
            Units = 500,
            EntryPrice = 100m,
            StopLoss = 82m,
            TakeProfit = 127m,
            AtrAtEntry = 9m,
            OpenCommission = 20m,
            EntryTime = new DateTimeOffset(2026, 1, 9, 10, 0, 0, Msk),
        };

        await using var h = new EngineHarness(Atr9Bars(longSignal: false));
        h.Positions.Current = position;
        h.TradeLogs.Realized = 1_234m;
        await h.StartAsync();

        await h.FeedAsync(Minute(12, 10, 0, 100m, 101m, 99m, 100m));
        Assert.Single(h.Journal.Closes);
        Assert.Single(h.Journal.Opens);

        // The stream re-sends the opening minute as it develops. The 10:00 low of 20
        // would stop the just-flipped SHORT at its TP (73) against the very bar it
        // entered on. It must be ignored: no second flip (signal consumed) and the
        // new leg is not checked against its own opening bar.
        await h.FeedAsync(Minute(12, 10, 0, 100m, 101m, 20m, 99m));

        Assert.Single(h.Journal.Closes);
        Assert.Equal(ExitReason.SignalReversal, h.Journal.Closes[0].ExitReason);
        Assert.Single(h.Journal.Opens);
        var current = h.Positions.Current;
        Assert.NotNull(current);
        Assert.Equal(SignalDirection.Short, current.Direction);
        Assert.Equal(100m, current.EntryPrice);
    }

    [Fact]
    public async Task ManualCloseFlattensAndRecordsTheTrade()
    {
        await using var h = new EngineHarness(Atr9Bars(longSignal: true));
        await h.StartAsync();
        await h.FeedAsync(Minute(12, 10, 0, 100m, 101m, 99m, 100m));
        Assert.Single(h.Journal.Opens);

        var closed = await h.Engine.ClosePositionAsync(105m, CancellationToken.None);

        Assert.NotNull(closed);
        Assert.Equal(ExitReason.Manual, closed!.Reason);
        Assert.Equal(105m, closed.ExitPrice);
        Assert.Single(h.Journal.Closes);
        Assert.Null(h.Positions.Current);
        Assert.Null(await h.Engine.ClosePositionAsync(105m, CancellationToken.None));
    }
}
