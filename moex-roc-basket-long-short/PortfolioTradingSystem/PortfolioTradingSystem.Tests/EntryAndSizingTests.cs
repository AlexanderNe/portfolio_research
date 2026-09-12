using Xunit;
using static PortfolioTradingSystem.Tests.TestHelpers;

namespace PortfolioTradingSystem.Tests;

public class EntryAndSizingTests
{
    [Fact]
    public void LongEntrySizingAndLevelsMatchResearch()
    {
        var st = Warmed(longSignal: true); // ATR 9, signal +1
        var p = st.TryOpen(100m, T0.AddMinutes(1));

        Assert.NotNull(p);
        // slDist = max(2*9, 100*1e-6) = 18
        // units = floor(10% * 100000 / 18) = 555, cap = floor(1*100000/100) = 1000
        Assert.Equal(555, p!.Units);
        Assert.Equal(100m, p.EntryPrice);
        Assert.Equal(82m, p.StopLoss);
        Assert.Equal(127m, p.TakeProfit);
        Assert.Equal(9m, p.AtrAtEntry);
        Assert.Equal(SignalDirection.Long, p.Direction);
        Assert.Equal(22.2m, p.OpenCommission);
        Assert.Equal(T0.AddMinutes(1), p.EntryTime);
        Assert.Equal(44477.8m, st.Cash); // 100000 - 555*100 - 22.2
    }

    [Fact]
    public void ShortEntrySizingAndLevelsMatchResearch()
    {
        var st = Warmed(longSignal: false); // ATR 9, signal -1
        var p = st.TryOpen(100m, T0.AddMinutes(1));

        Assert.NotNull(p);
        Assert.Equal(555, p!.Units);
        Assert.Equal(SignalDirection.Short, p.Direction);
        Assert.Equal(118m, p.StopLoss); // 100 + 2*9
        Assert.Equal(73m, p.TakeProfit); // 100 - 3*9
        Assert.Equal(155477.8m, st.Cash); // 100000 + 555*100 - 22.2
    }

    [Fact]
    public void LeverageCapBindsWhenRiskSizingExceedsIt()
    {
        var opts = Defaults();
        opts.SlAtr = 0.1m;
        var st = New(opts);
        st.WarmUp(Atr9Bars(longSignal: true)); // ATR 9, signal +1

        var p = st.TryOpen(100m, T0.AddMinutes(1));
        Assert.NotNull(p);
        // slDist = max(0.1*9, 100e-6) = 0.9 -> floor(10000/0.9) = 11111, capped by
        // floor(100000 / (100 * 1.0004)) = 999: the cap leaves room for the opening
        // commission, which is paid out of the same cash.
        Assert.Equal(999, p!.Units);
    }

    [Fact]
    public void EntryRequiresWarmUp()
    {
        var st = New();
        st.WarmUp(Closes(100, 100, 100));
        Assert.Null(st.TryOpen(100m, T0));
    }

    [Fact]
    public void FlatBarsProduceNoEntryEvenWhenWarmed()
    {
        var st = New();
        st.WarmUp(Closes(100, 100, 100, 100, 100, 100));
        Assert.Null(st.TryOpen(100m, T0));
    }

    [Fact]
    public void NoReentryWhilePositionIsOpen()
    {
        var st = Warmed(longSignal: true);
        var first = st.TryOpen(100m, T0);
        Assert.NotNull(first);
        Assert.Null(st.TryOpen(101m, T0.AddMinutes(1)));
        Assert.Same(first, st.Position);
    }

    [Fact]
    public void EntryConsumesPendingSignalAndUsesCurrentBarOpen()
    {
        var st = New();
        st.WarmUp(Atr9Bars(longSignal: true));
        Assert.Equal(1, st.PendingSignal);

        st.FinalizeDay(Day(6, 16, 7, 10)); // roc = 10/7 - 1 > 0.01 -> +1 again
        var p = st.TryOpen(50m, T0.AddMinutes(1)); // next bar open, not the 6th close
        Assert.NotNull(p);
        Assert.Equal(50m, p!.EntryPrice);
        Assert.Null(st.PendingSignal);
    }

    [Fact]
    public void ShortSignalSuppressedWhenShortsDisabled()
    {
        var opts = Defaults();
        opts.ShortsEnabled = false;
        var st = New(opts);
        st.WarmUp(Atr9Bars(longSignal: false)); // signal -1
        Assert.Equal(-1, st.PendingSignal);

        Assert.Null(st.TryOpen(100m, T0));
        Assert.Null(st.Position);
        Assert.Null(st.PendingSignal); // consumed on the failed attempt
    }
}