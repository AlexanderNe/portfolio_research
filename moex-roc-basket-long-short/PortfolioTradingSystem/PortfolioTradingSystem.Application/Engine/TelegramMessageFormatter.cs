using System.Globalization;
using PortfolioTradingSystem.Domain.Enums;
using PortfolioTradingSystem.Domain.Strategy;

namespace PortfolioTradingSystem.Application.Engine;

/// <summary>Builds the Telegram channel messages for opened/closed signals.</summary>
public sealed class TelegramMessageFormatter
{
    public string Opened(TradeOpenedEvent e)
    {
        string dir = e.Direction == SignalDirection.Long ? "LONG" : "SHORT";
        return string.Join('\n',
            $"OPEN {dir} {e.Ticker}",
            $"  entry:       {F(e.EntryPrice)} RUB",
            $"  stop-loss:   {F(e.StopLoss)} RUB",
            $"  take-profit: {F(e.TakeProfit)} RUB",
            $"  units:       {e.Units}",
            $"  time:        {T(e.EntryTime)} MSK");
    }

    public string Closed(TradeClosedEvent e)
    {
        string dir = e.Direction == SignalDirection.Long ? "LONG" : "SHORT";
        string pnlPct = e.ReturnPercent >= 0 ? $"+{e.ReturnPercent:0.00}%" : $"{e.ReturnPercent:0.00}%";
        return string.Join('\n',
            $"CLOSE {dir} {e.Ticker}",
            $"  entry:       {F(e.EntryPrice)} RUB",
            $"  exit:        {F(e.ExitPrice)} RUB",
            $"  pnl:         {pnlPct} ({e.PnlRub:+0;-#;0} RUB)",
            $"  reason:      {Reason(e.Reason)}",
            $"  time:        {T(e.ExitTime)} MSK");
    }

    private static string Reason(ExitReason reason) => reason switch
    {
        ExitReason.StopLoss => "stop-loss",
        ExitReason.TakeProfit => "take-profit",
        ExitReason.SignalReversal => "signal reversal",
        ExitReason.Manual => "manual",
        ExitReason.System => "system",
        _ => reason.ToString(),
    };

    private static string F(decimal v) => v.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string T(DateTimeOffset utc) => utc.ToOffset(MoscowClock.Offset).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
}