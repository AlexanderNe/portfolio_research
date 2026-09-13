using PortfolioTradingSystem.Domain.Enums;
using PortfolioTradingSystem.Domain.Models;
using Xunit;
using static PortfolioTradingSystem.Tests.TestHelpers;

namespace PortfolioTradingSystem.Tests;

/// <summary>Fills that have to be reachable in the real market.</summary>
public class ExecutionModelTests
{
    [Fact]
    public void LongStopGappedThroughFillsAtTheOpenNotTheLevel()
    {
        var st = Warmed(longSignal: true);
        st.TryOpen(100m, T0); // SL 82

        // The bar opens at 70, far below the stop: 82 was never available.
        var e = st.CheckStop(high: 75m, low: 68m, time: T0.AddDays(1), open: 70m);

        Assert.Equal(ExitReason.StopLoss, e!.Reason);
        Assert.Equal(70m, e.ExitPrice);
    }

    [Fact]
    public void ShortStopGappedThroughFillsAtTheOpen()
    {
        var st = Warmed(longSignal: false);
        st.TryOpen(100m, T0); // SL 118

        var e = st.CheckStop(high: 135m, low: 128m, time: T0.AddDays(1), open: 130m);

        Assert.Equal(ExitReason.StopLoss, e!.Reason);
        Assert.Equal(130m, e.ExitPrice);
    }

    [Fact]
    public void TargetGappedIntoFillsAtTheOpen()
    {
        var st = Warmed(longSignal: true);
        st.TryOpen(100m, T0); // TP 127

        var e = st.CheckStop(high: 140m, low: 131m, time: T0.AddDays(1), open: 133m);

        Assert.Equal(ExitReason.TakeProfit, e!.Reason);
        Assert.Equal(133m, e.ExitPrice);
    }

    [Fact]
    public void LevelIsUsedWhenTheBarOpensInsideIt()
    {
        var st = Warmed(longSignal: true);
        st.TryOpen(100m, T0);

        var e = st.CheckStop(high: 101m, low: 80m, time: T0.AddDays(1), open: 99m);

        Assert.Equal(82m, e!.ExitPrice);
    }

    [Fact]
    public void SizeIsRoundedDownToWholeLots()
    {
        var st = New(Defaults());
        var withLots = new PortfolioTradingSystem.Domain.Strategy.MomentumEngineState(Defaults(), "TST", lotSize: 10);
        st.WarmUp(Atr9Bars(longSignal: true));
        withLots.WarmUp(Atr9Bars(longSignal: true));

        var single = st.TryOpen(100m, T0);
        var lots = withLots.TryOpen(100m, T0);

        Assert.Equal(555, single!.Units);   // risk-based size
        Assert.Equal(550, lots!.Units);     // 55 lots of 10
    }

    [Fact]
    public void NoPositionIsOpenedWhenLessThanOneLotFits()
    {
        var options = Defaults();
        options.InitialCapital = 1_000m;
        var st = new PortfolioTradingSystem.Domain.Strategy.MomentumEngineState(options, "TST", lotSize: 1_000);
        st.WarmUp(Atr9Bars(longSignal: true));

        Assert.Null(st.TryOpen(100m, T0));
        Assert.Null(st.Position);
    }

    [Fact]
    public void PartialSessionFeedsRocButNotAtr()
    {
        var complete = New();
        var partial = New();
        complete.WarmUp(Atr9Bars(longSignal: true));
        partial.WarmUp(Atr9Bars(longSignal: true));
        Assert.Equal(9m, complete.Atr);

        var bar = new Candle(T0.AddDays(6), 18m, 21m, 3m, 18m, 0L);
        complete.FinalizeDay(bar);
        partial.FinalizeDay(bar with { IsComplete = false });

        // The close is real on both, so ROC is identical...
        Assert.Equal(complete.RoC, partial.RoC);
        Assert.Equal(complete.PendingSignal, partial.PendingSignal);
        // ...but a range the engine only half observed must not move ATR.
        AssertAtrEqual(complete.Atr, (18m + 13m * 9m) / 14m);
        Assert.Equal(9m, partial.Atr);
    }

    [Fact]
    public void ZeroPriceDoesNotThrow()
    {
        var st = Warmed(longSignal: true);
        Assert.Null(st.TryOpen(0m, T0));
        Assert.Null(st.Position);
    }

    [Fact]
    public void NotionalPlusCommissionNeverExceedsCash()
    {
        var options = Defaults();
        options.SlAtr = 0.01m; // force the leverage cap to bind
        var st = new PortfolioTradingSystem.Domain.Strategy.MomentumEngineState(options, "TST");
        st.WarmUp(Atr9Bars(longSignal: true));

        var p = st.TryOpen(100m, T0);

        decimal notional = p!.Units * p.EntryPrice;
        Assert.True(notional + p.OpenCommission <= options.InitialCapital,
            $"notional {notional} + commission {p.OpenCommission} exceeds cash {options.InitialCapital}");
        Assert.True(st.Cash >= 0m, $"cash went negative: {st.Cash}");
    }
}
