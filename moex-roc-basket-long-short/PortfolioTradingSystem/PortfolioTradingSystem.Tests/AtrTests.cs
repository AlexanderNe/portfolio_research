using Xunit;
using static PortfolioTradingSystem.Tests.TestHelpers;

namespace PortfolioTradingSystem.Tests;

public class AtrTests
{
    [Fact]
    public void SeedsAtrFromFirstBarTrueRange()
    {
        var st = New();
        st.WarmUp(new[] { Day(0, 10, 9, 9.5m) });
        Assert.Equal(1m, st.Atr);
    }

    [Fact]
    public void AtrUsesPreviousCloseInTrueRangeWithEwmSmoothing()
    {
        var st = New();
        st.WarmUp(new[]
        {
            Day(0, 10, 9, 9.5m),       // TR seed = high-low = 1 -> ATR = 1
            Day(1, 11, 10.5m, 10.8m),  // TR = max(0.5, |11-9.5|=1.5, |10.5-9.5|=1) = 1.5
            Day(2, 12, 11, 11.5m),     // TR = max(1, |12-10.8|=1.2, |11-10.8|=0.2) = 1.2
        });

        // ATR3 = (1.2 + 13 * ATR2)/14, ATR2 = (1.5 + 13 * 1)/14
        var atr3 = (1.2m + 13m * ((1.5m + 13m) / 14m)) / 14m;
        AssertAtrEqual(st.Atr, atr3, 1e-9m);

        st.FinalizeDay(Day(3, 19, 15, 20)); // TR = max(4, |19-11.5|=7.5, |15-11.5|=3.5) = 7.5
        var atr4 = (7.5m + 13m * atr3) / 14m;
        AssertAtrEqual(st.Atr, atr4, 1e-9m);
    }

    [Fact]
    public void RemainsConstantForConstantTrueRange()
    {
        var st = New();
        st.WarmUp(Atr9Bars(longSignal: true));
        Assert.Equal(9m, st.Atr);
    }
}