using Xunit;
using static PortfolioTradingSystem.Tests.TestHelpers;

namespace PortfolioTradingSystem.Tests;

public class CashAndEquityTests
{
    [Fact]
    public void LongTradeCashAndPnlFollowSimulator()
    {
        var st = Warmed(longSignal: true);
        st.TryOpen(100m, T0);
        Assert.Equal(44477.8m, st.Cash);

        var e = st.CheckStop(128m, 120m, T0.AddMinutes(1)); // TP 127
        Assert.Equal(127m, e!.ExitPrice);
        // pnl = (127-100)*555 - 28.194 - 22.2 = 14934.606
        Assert.Equal(14934.606m, e.PnlRub);
        Assert.Equal(50.394m, e.Commission);
        Assert.Equal(14934.606m / 55500m * 100m, e.ReturnPercent);
        Assert.Equal(114934.606m, st.Cash); // 44477.8 + 127*555 - 28.194
        Assert.Equal(114934.606m, st.Equity(127m)); // flat
    }

    [Fact]
    public void ShortTradeCashAndPnlFollowSimulator()
    {
        var st = Warmed(longSignal: false);
        st.TryOpen(100m, T0);
        Assert.Equal(155477.8m, st.Cash);

        var e = st.CheckStop(200m, 50m, T0.AddMinutes(1)); // SL 118
        Assert.Equal(ExitReason.StopLoss, e!.Reason);
        // pnl = (118-100)*555*(-1) - 26.196 - 22.2 = -10038.396
        Assert.Equal(-10038.396m, e.PnlRub);
        Assert.Equal(89961.604m, st.Cash); // 155477.8 - 118*555 - 26.196
    }

    [Fact]
    public void EquityMarksOpenPositionAlongPrice()
    {
        var st = Warmed(longSignal: true);
        st.TryOpen(100m, T0);
        Assert.Equal(99977.8m, st.Equity(100m));  // 44477.8 + 555*100
        Assert.Equal(105527.8m, st.Equity(110m)); // +10 pts
        Assert.Equal(114962.8m, st.Equity(127m)); // TP price
    }

    [Fact]
    public void EquityIsCashWhenFlat()
    {
        var st = Warmed(longSignal: true);
        Assert.Equal(100000m, st.Equity(100m));
    }

    [Fact]
    public void RestoredPositionProvidesSameExitMath()
    {
        var st = New();
        st.SetCash(100000m);
        st.RestorePosition(new OpenPosition
        {
            Direction = SignalDirection.Long,
            Units = 1000,
            EntryPrice = 100m,
            StopLoss = 82m,
            TakeProfit = 127m,
            AtrAtEntry = 9m,
            OpenCommission = 40m,
            EntryTime = T0,
        });

        Assert.Equal(200000m, st.Equity(100m));

        var e = st.CheckStop(200m, 50m, T0.AddMinutes(1)); // SL 82 first
        Assert.Equal(ExitReason.StopLoss, e!.Reason);
        Assert.Equal(82m, e.ExitPrice);
        // pnl = (82-100)*1000 - 32.8 - 40 = -18072.8
        Assert.Equal(-18072.8m, e.PnlRub);
        Assert.Equal(181967.2m, st.Cash); // 100000 + 82*1000 - 32.8
    }
}