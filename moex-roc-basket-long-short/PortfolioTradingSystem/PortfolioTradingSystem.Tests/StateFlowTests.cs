using Xunit;
using static PortfolioTradingSystem.Tests.TestHelpers;

namespace PortfolioTradingSystem.Tests;

public class StateFlowTests
{
    [Fact]
    public void FinalizeDayEqualsWarmUpForIncrementalFeeding()
    {
        var full = Atr9Bars(longSignal: true);

        var oneShot = New();
        oneShot.WarmUp(full);

        var incremental = New();
        incremental.WarmUp(full.Take(full.Count - 1));
        incremental.FinalizeDay(full[^1]);

        Assert.Equal(oneShot.Atr, incremental.Atr);
        Assert.Equal(oneShot.RoC, incremental.RoC);
        Assert.Equal(oneShot.PendingSignal, incremental.PendingSignal);
    }

    [Fact]
    public void PositionSurvivesFinalizeDayWhileAtrUpdates()
    {
        var st = Warmed(longSignal: true);
        var p = st.TryOpen(100m, T0);
        Assert.NotNull(p);
        Assert.Equal(9m, st.Atr);

        st.FinalizeDay(Day(6, 21, 3, 18)); // prev close 15 -> TR = max(18, 6, 12) = 18
        Assert.NotNull(st.Position);
        AssertAtrEqual(st.Atr, (18m + 13m * 9m) / 14m);
    }

    [Fact]
    public void SnapshotReflectsStateBeforeAndAfterEntry()
    {
        var st = Warmed(longSignal: true);
        var before = st.GetSnapshot(100m);

        Assert.True(before.IsWarmedUp);
        Assert.False(before.PositionDirection.HasValue);
        Assert.Equal(100000m, before.Cash);
        Assert.Equal(100000m, before.Equity);
        Assert.Equal(9m, before.Atr);
        Assert.Equal(2m, before.RoC); // 15/5 - 1
        Assert.Equal(1, before.PendingSignal);

        st.TryOpen(100m, T0);
        var after = st.GetSnapshot(127m);

        Assert.Equal(SignalDirection.Long, after.PositionDirection);
        Assert.Equal(555, after.PositionUnits);
        Assert.Equal(100m, after.EntryPrice);
        Assert.Equal(82m, after.StopLoss);
        Assert.Equal(127m, after.TakeProfit);
        Assert.Equal(T0, after.EntryTime);
        Assert.Equal(44477.8m, after.Cash);
        Assert.Equal(114962.8m, after.Equity); // 44477.8 + 555*127
        Assert.Null(after.PendingSignal);
    }
}