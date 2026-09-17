using PortfolioTradingSystem.Application.Engine;
using PortfolioTradingSystem.Domain.Models;

namespace PortfolioTradingSystem.Tests;

public class TelegramMessageFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
    private readonly TelegramMessageFormatter _formatter = new();

    [Fact]
    public void OpenedLong_HasGreenIconAndBoldHeading()
    {
        string text = _formatter.Opened(new TradeOpenedEvent(
            "SBER", SignalDirection.Long, 356, 280.5000m, 275.0000m, 291.0000m, 12.5m, Now));

        Assert.Contains("\U0001F7E2 OPEN LONG", text);
        Assert.Contains("<b>", text);
        Assert.Contains("<code>SBER</code>", text);
        Assert.Contains("<b>Qty:</b>", text);
        Assert.Contains("<code>356</code>", text);
    }

    [Fact]
    public void OpenedShort_HasRedIcon()
    {
        string text = _formatter.Opened(new TradeOpenedEvent(
            "SBER", SignalDirection.Short, 356, 280.5000m, 285.0000m, 270.0000m, 12.5m, Now));

        Assert.Contains("\U0001F534 OPEN SHORT", text);
    }

    [Fact]
    public void ClosedProfit_HasGreenIconAndPlusPercent()
    {
        string text = _formatter.Closed(new TradeClosedEvent(
            "SBER", SignalDirection.Long, 356, 280.0000m, 285.0000m,
            ExitReason.TakeProfit, 1780m, 1.7857m, 20m, Now, Now.AddDays(1)));

        Assert.Contains("\U0001F51A CLOSE LONG", text);
        Assert.Contains("+1.79%", text);
        Assert.Contains("\U0001F7E2 +1.79%", text);
        Assert.Contains("(+1780 \u20BD)", text);
        Assert.Contains("\U0001F3AF take-profit", text);
    }

    [Fact]
    public void ClosedLoss_HasRedIconAndMinusPercent()
    {
        string text = _formatter.Closed(new TradeClosedEvent(
            "SBER", SignalDirection.Long, 356, 280.0000m, 274.0000m,
            ExitReason.StopLoss, -2136m, -2.1428m, 20m, Now, Now.AddDays(1)));

        Assert.Contains("\U0001F534 -2.14%", text);
        Assert.Contains("(-2136 \u20BD)", text);
        Assert.Contains("\U0001F6D1 stop-loss", text);
    }

    [Fact]
    public void FromLog_Closed_AddsIconsAndEscapesTicker()
    {
        var log = new SignalLogEntry
        {
            Ticker = "A&B <CO>",
            Type = SignalType.PositionClosed,
            Direction = SignalDirection.Short,
            ExitPrice = 99.5m,
            PnlPercent = -1.25m,
            ExitReason = ExitReason.SignalReversal,
            Timestamp = Now,
        };

        string text = _formatter.FromLog(log);

        Assert.Contains("\U0001F51A CLOSE SHORT", text);
        Assert.Contains("A&amp;B &lt;CO&gt;", text);
        Assert.Contains("\U0001F504 signal reversal", text);
    }

    [Fact]
    public void Duplicate_PrependsRemarkAndKeepsHtml()
    {
        var log = new SignalLogEntry
        {
            Ticker = "SBER",
            Type = SignalType.PositionOpened,
            Direction = SignalDirection.Long,
            Price = 280.5m,
            Timestamp = Now,
        };

        string text = _formatter.Duplicate(log);

        Assert.StartsWith(TelegramMessageFormatter.DuplicateRemark, text);
        Assert.Contains("\U0001F7E2 OPEN LONG", text);
        Assert.Contains("<b>", text);
    }
}