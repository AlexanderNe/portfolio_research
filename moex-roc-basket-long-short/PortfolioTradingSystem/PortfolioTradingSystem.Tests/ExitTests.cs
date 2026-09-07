using Xunit;
using static PortfolioTradingSystem.Tests.TestHelpers;

namespace PortfolioTradingSystem.Tests;

public class ExitTests
{
    private static MomentumEngineState OpenLong()
    {
        var st = Warmed(longSignal: true);
        st.TryOpen(100m, T0);
        return st;
    }

    private static MomentumEngineState OpenShort()
    {
        var st = Warmed(longSignal: false);
        st.TryOpen(100m, T0);
        return st;
    }

    [Fact]
    public void LongStopLossExitAtLevel()
    {
        var st = OpenLong();
        var e = st.CheckStop(101m, 80m, T0.AddMinutes(1));
        Assert.NotNull(e);
        Assert.Equal(ExitReason.StopLoss, e!.Reason);
        Assert.Equal(82m, e.ExitPrice);
        Assert.Null(st.Position);
    }

    [Fact]
    public void LongTakeProfitExitAtLevel()
    {
        var st = OpenLong();
        var e = st.CheckStop(128m, 120m, T0.AddMinutes(1));
        Assert.NotNull(e);
        Assert.Equal(ExitReason.TakeProfit, e!.Reason);
        Assert.Equal(127m, e.ExitPrice);
    }

    [Fact]
    public void LongStopLossWinsWhenBothLevelsHitInOneBar()
    {
        var st = OpenLong();
        var e = st.CheckStop(130m, 80m, T0.AddMinutes(1));
        Assert.Equal(ExitReason.StopLoss, e!.Reason);
        Assert.Equal(82m, e.ExitPrice);
    }

    [Fact]
    public void LongExitAtLevelNotCandleExtreme()
    {
        var st = OpenLong();
        var e = st.CheckStop(200m, 100m, T0.AddMinutes(1));
        Assert.Equal(ExitReason.TakeProfit, e!.Reason);
        Assert.Equal(127m, e.ExitPrice);
    }

    [Fact]
    public void ShortTakeProfitExitAtLevel()
    {
        var st = OpenShort();
        var e = st.CheckStop(110m, 70m, T0.AddMinutes(1));
        Assert.Equal(ExitReason.TakeProfit, e!.Reason);
        Assert.Equal(73m, e.ExitPrice);
    }

    [Fact]
    public void ShortStopLossWinsWhenBothLevelsHitInOneBar()
    {
        var st = OpenShort();
        var e = st.CheckStop(200m, 50m, T0.AddMinutes(1));
        Assert.Equal(ExitReason.StopLoss, e!.Reason);
        Assert.Equal(118m, e.ExitPrice);
    }

    [Fact]
    public void NoExitWhenRangeStaysWithinLevels()
    {
        var st = OpenLong();
        var e = st.CheckStop(126m, 83m, T0.AddMinutes(1));
        Assert.Null(e);
        Assert.NotNull(st.Position);
    }

    [Fact]
    public void ClosePositionForcesExitWithResearchFormula()
    {
        var st = OpenLong();
        var e = st.ClosePosition(90m, ExitReason.SignalReversal, T0.AddMinutes(1));

        Assert.Equal(ExitReason.SignalReversal, e!.Reason);
        Assert.Equal(555, e.Units);
        Assert.Equal(90m, e.ExitPrice);
        // pnl = (90-100)*555 - closeComm(19.98) - openComm(22.2) = -5592.18
        Assert.Equal(-5592.18m, e.PnlRub);
        Assert.Equal(42.18m, e.Commission);
        Assert.Equal(94407.82m, st.Cash); // 44477.8 + 90*555 - 19.98
        Assert.Null(st.Position);
    }
}