using static PortfolioTradingSystem.Tests.TestHelpers;

namespace PortfolioTradingSystem.Tests;

/// <summary>
/// The research "signal_reversal": a position is flipped at the next bar's open
/// when that bar's previous close signalled the OPPOSITE direction (reason
/// "signal_reversal"); the re-entry at the same open is the one exception to
/// "a bar that produced an exit can not also produce an entry".
/// </summary>
public class ReversalTests
{
    [Fact]
    public void ReversalClosesAtOpenAndReopensOppositeAtSameOpen()
    {
        var st = Warmed(longSignal: true);
        var p = st.TryOpen(100m, T0);
        Assert.Equal(SignalDirection.Long, p!.Direction);

        st.FinalizeDay(Day(6, 6, 6, 6)); // ROC = 6/7 - 1 < -1% -> pending -1 (short)

        var closed = st.TryCloseOnReversal(50m, T0.AddDays(1));

        Assert.NotNull(closed);
        Assert.Equal(ExitReason.SignalReversal, closed!.Reason);
        Assert.Equal(50m, closed.ExitPrice);
        Assert.Null(st.Position);

        var reopened = st.TryOpen(50m, T0.AddDays(1));

        Assert.NotNull(reopened);
        Assert.Equal(SignalDirection.Short, reopened!.Direction);
        Assert.Equal(50m, reopened.EntryPrice);
        Assert.Null(st.PendingSignal);
    }

    [Fact]
    public void ReversalIgnoredWhenPendingSignalMatchesThePosition()
    {
        var st = Warmed(longSignal: true);
        st.TryOpen(100m, T0);

        st.FinalizeDay(Day(6, 100, 100, 100)); // ROC = 100/7 - 1 > +1% -> pending +1 (long again)

        Assert.Null(st.TryCloseOnReversal(50m, T0.AddDays(1)));
        Assert.NotNull(st.Position);
    }

    [Fact]
    public void ReversalIgnoredWhenShortsAreDisabled()
    {
        var options = Defaults();
        options.ShortsEnabled = false;
        var st = New(options);
        st.WarmUp(Atr9Bars(longSignal: true));
        st.TryOpen(100m, T0);

        st.FinalizeDay(Day(6, 6, 6, 6)); // pending -1 (short)

        Assert.Null(st.TryCloseOnReversal(50m, T0.AddDays(1)));
        Assert.NotNull(st.Position);
    }

    [Fact]
    public void ReversalIgnoredWhenFlatOrNotWarmedUp()
    {
        Assert.Null(Warmed(longSignal: false).TryCloseOnReversal(50m, T0));
        Assert.Null(New().TryCloseOnReversal(50m, T0));
    }
}