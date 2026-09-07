using Xunit;
using static PortfolioTradingSystem.Tests.TestHelpers;

namespace PortfolioTradingSystem.Tests;

public class RocSignalTests
{
    [Fact]
    public void RequiresRocBarsPlusOneCloses()
    {
        var st = New();
        st.WarmUp(Closes(100, 100, 100, 100, 100));
        Assert.False(st.IsWarmedUp);
        Assert.Null(st.PendingSignal);
        Assert.Equal(0m, st.RoC);
    }

    [Fact]
    public void WarmUpCompletesAtSixthClose()
    {
        var st = New();
        st.WarmUp(Closes(100, 100, 100, 100, 100));
        Assert.False(st.IsWarmedUp);

        st.FinalizeDay(Day(5, 100, 100, 100));
        Assert.True(st.IsWarmedUp);
        Assert.Equal(0m, st.RoC);
        Assert.Equal(0, st.PendingSignal);
    }

    [Fact]
    public void ExactThresholdProducesNoSignal()
    {
        var st = New();
        st.WarmUp(Closes(100, 100, 100, 100, 100, 101));
        Assert.Equal(0.01m, st.RoC);
        Assert.Equal(0, st.PendingSignal);
    }

    [Fact]
    public void PositiveBreakoutSignalsLong()
    {
        var st = New();
        st.WarmUp(Closes(100, 100, 100, 100, 100, 103));
        Assert.Equal(0.03m, st.RoC);
        Assert.Equal(1, st.PendingSignal);
    }

    [Fact]
    public void NegativeBreakoutSignalsShort()
    {
        var st = New();
        st.WarmUp(Closes(100, 100, 100, 100, 100, 97));
        Assert.Equal(-0.03m, st.RoC);
        Assert.Equal(-1, st.PendingSignal);
    }

    [Fact]
    public void SignalUsesFixedWindowOfLastNPlusOneCloses()
    {
        var st = New();
        st.WarmUp(Closes(100, 101, 102, 103, 104, 105));
        Assert.Equal(1, st.PendingSignal); // 105/100 - 1 = 0.05

        st.FinalizeDay(Day(6, 10, 10, 10)); // window slides: 101,102,103,104,105,10
        Assert.Equal(10m / 101m - 1m, st.RoC);
        Assert.Equal(-1, st.PendingSignal);
    }
}