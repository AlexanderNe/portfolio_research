using System.Globalization;
using PortfolioTradingSystem.Domain.Enums;
using PortfolioTradingSystem.Domain.Models;
using PortfolioTradingSystem.Domain.Strategy;

namespace PortfolioTradingSystem.Application.Engine;

/// <summary>Builds the Telegram channel messages for opened/closed signals.</summary>
public sealed class TelegramMessageFormatter
{
    /// <summary>Prefix attached to every manually re-sent signal so recipients know it is a duplicate.</summary>
    public const string DuplicateRemark =
        "\u26A0\uFE0F This is a duplicate of a previously sent signal \u2014 NOT a new signal.";

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

    /// <summary>
    /// Renders a persisted <see cref="SignalLogEntry"/> as an "OPEN"/"CLOSE" signal
    /// message (fields available in the audit log, no live engine state).
    /// </summary>
    public string FromLog(SignalLogEntry e) => e.Type switch
    {
        SignalType.PositionOpened => OpenedFromLog(e),
        SignalType.PositionClosed => ClosedFromLog(e),
        _ => $"{e.Type} {e.Ticker} at {F(e.Price)} RUB ({T(e.Timestamp)} MSK)",
    };

    /// <summary>Signal text prefixed with the "duplicate, not a new signal" remark.</summary>
    public string Duplicate(SignalLogEntry e) => DuplicateRemark + "\n\n" + FromLog(e);

    private static string OpenedFromLog(SignalLogEntry e)
    {
        string dir = e.Direction == SignalDirection.Long ? "LONG" : "SHORT";
        return string.Join('\n',
            $"OPEN {dir} {e.Ticker}",
            $"  entry:       {F(e.Price)} RUB",
            $"  stop-loss:   {F(e.StopLoss)} RUB",
            $"  take-profit: {F(e.TakeProfit)} RUB",
            $"  time:        {T(e.Timestamp)} MSK");
    }

    private static string ClosedFromLog(SignalLogEntry e)
    {
        string dir = e.Direction == SignalDirection.Long ? "LONG" : "SHORT";
        string pnlPct = e.PnlPercent is { } pnl ? (pnl >= 0 ? $"+{pnl:0.00}%" : $"{pnl:0.00}%") : "\u2014";
        return string.Join('\n',
            $"CLOSE {dir} {e.Ticker}",
            $"  exit:        {F(e.ExitPrice)} RUB",
            $"  pnl:         {pnlPct}",
            $"  reason:      {Reason(e.ExitReason)}",
            $"  time:        {T(e.Timestamp)} MSK");
    }

    private static string Reason(ExitReason? reason) => reason is { } r ? Reason(r) : "\u2014";

    private static string Reason(ExitReason reason) => reason switch
    {
        ExitReason.StopLoss => "stop-loss",
        ExitReason.TakeProfit => "take-profit",
        ExitReason.SignalReversal => "signal reversal",
        ExitReason.Manual => "manual",
        ExitReason.System => "system",
        _ => reason.ToString(),
    };

    private static string F(decimal? v) => v is { } value ? F(value) : "\u2014";

    private static string F(decimal v) => v.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string T(DateTimeOffset utc) => utc.ToOffset(MoscowClock.Offset).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
}