using System.Globalization;
using PortfolioTradingSystem.Domain.Enums;
using PortfolioTradingSystem.Domain.Models;
using PortfolioTradingSystem.Domain.Strategy;

namespace PortfolioTradingSystem.Application.Engine;

/// <summary>
/// Builds the readable Telegram channel messages for opened/closed signals:
/// HTML parse mode with icons, bold labels and monospace values.
/// </summary>
public sealed class TelegramMessageFormatter
{
    /// <summary>Prefix attached to every manually re-sent signal so recipients know it is a duplicate.</summary>
    public const string DuplicateRemark =
        "\u26A0\uFE0F This is a duplicate of a previously sent signal \u2014 NOT a new signal.";

    private const string GreenIcon = "\U0001F7E2";     // 🟢
    private const string RedIcon = "\U0001F534";       // 🔴
    private const string CloseIcon = "\U0001F51A";     // 🔚
    private const string StopIcon = "\U0001F6D1";      // 🛑
    private const string TargetIcon = "\U0001F3AF";    // 🎯
    private const string ReversalIcon = "\U0001F504";  // 🔄
    private const string PersonIcon = "\U0001F464";    // 👤
    private const string GearIcon = "\u2699\uFE0F";    // ⚙️
    private const string NeutralIcon = "\u26AA";       // ⚪

    public string Opened(TradeOpenedEvent e)
    {
        string dir = e.Direction == SignalDirection.Long ? "LONG" : "SHORT";
        return string.Join('\n',
            $"<b>{DirectionIcon(e.Direction)} OPEN {dir}</b> <code>{E(e.Ticker)}</code>",
            $"<b>Entry:</b>  <code>{F(e.EntryPrice)}</code> \u20BD",
            $"<b>Stop-loss:</b>  <code>{F(e.StopLoss)}</code> \u20BD",
            $"<b>Take-profit:</b>  <code>{F(e.TakeProfit)}</code> \u20BD",
            $"<b>Qty:</b>  <code>{e.Units}</code>",
            $"<b>Time:</b>  <code>{T(e.EntryTime)}</code> MSK");
    }

    public string Closed(TradeClosedEvent e)
    {
        string dir = e.Direction == SignalDirection.Long ? "LONG" : "SHORT";
        string pnlPct = (e.ReturnPercent >= 0 ? "+" : "")
            + e.ReturnPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%";
        bool gain = e.PnlRub >= 0;
        return string.Join('\n',
            $"<b>{CloseIcon} CLOSE {dir}</b> <code>{E(e.Ticker)}</code>",
            $"<b>Entry:</b>  <code>{F(e.EntryPrice)}</code> \u20BD",
            $"<b>Exit:</b>  <code>{F(e.ExitPrice)}</code> \u20BD",
            $"<b>PnL:</b>  <b>{GainLossIcon(gain)} {pnlPct}</b> ({e.PnlRub:+0;-#;0} \u20BD)",
            $"<b>Reason:</b>  {ReasonWithIcon(e.Reason)}",
            $"<b>Time:</b>  <code>{T(e.ExitTime)}</code> MSK");
    }

    /// <summary>
    /// Renders a persisted <see cref="SignalLogEntry"/> as an "OPEN"/"CLOSE" signal
    /// message (fields available in the audit log, no live engine state).
    /// </summary>
    public string FromLog(SignalLogEntry e) => e.Type switch
    {
        SignalType.PositionOpened => OpenedFromLog(e),
        SignalType.PositionClosed => ClosedFromLog(e),
        _ => $"{e.Type} {E(e.Ticker)} at {F(e.Price)} RUB ({T(e.Timestamp)} MSK)",
    };

    /// <summary>Signal text prefixed with the "duplicate, not a new signal" remark.</summary>
    public string Duplicate(SignalLogEntry e) => DuplicateRemark + "\n\n" + FromLog(e);

    private static string OpenedFromLog(SignalLogEntry e)
    {
        string dir = e.Direction == SignalDirection.Long ? "LONG" : "SHORT";
        return string.Join('\n',
            $"<b>{DirectionIcon(e.Direction)} OPEN {dir}</b> <code>{E(e.Ticker)}</code>",
            $"<b>Entry:</b>  <code>{F(e.Price)}</code> \u20BD",
            $"<b>Stop-loss:</b>  <code>{F(e.StopLoss)}</code> \u20BD",
            $"<b>Take-profit:</b>  <code>{F(e.TakeProfit)}</code> \u20BD",
            $"<b>Time:</b>  <code>{T(e.Timestamp)}</code> MSK");
    }

    private static string ClosedFromLog(SignalLogEntry e)
    {
        string dir = e.Direction == SignalDirection.Long ? "LONG" : "SHORT";
        bool gain = e.PnlPercent is { } pnl && pnl >= 0;
        string pnlPct = e.PnlPercent is { } pnlVal
            ? (pnlVal >= 0 ? "+" : "") + pnlVal.ToString("0.00", CultureInfo.InvariantCulture) + "%"
            : "\u2014";
        return string.Join('\n',
            $"<b>{CloseIcon} CLOSE {dir}</b> <code>{E(e.Ticker)}</code>",
            $"<b>Exit:</b>  <code>{F(e.ExitPrice)}</code> \u20BD",
            $"<b>PnL:</b>  <b>{GainLossIcon(gain)} {pnlPct}</b>",
            $"<b>Reason:</b>  {ReasonWithIcon(e.ExitReason)}",
            $"<b>Time:</b>  <code>{T(e.Timestamp)}</code> MSK");
    }

    private static string DirectionIcon(SignalDirection direction) =>
        direction == SignalDirection.Long ? GreenIcon : RedIcon;

    private static string GainLossIcon(bool gain) => gain ? GreenIcon : RedIcon;

    private static string ReasonWithIcon(ExitReason? reason) => reason is { } r ? ReasonWithIcon(r) : $"{NeutralIcon} \u2014";

    private static string ReasonWithIcon(ExitReason reason) => reason switch
    {
        ExitReason.StopLoss => $"{StopIcon} stop-loss",
        ExitReason.TakeProfit => $"{TargetIcon} take-profit",
        ExitReason.SignalReversal => $"{ReversalIcon} signal reversal",
        ExitReason.Manual => $"{PersonIcon} manual",
        ExitReason.System => $"{GearIcon} system",
        _ => $"{NeutralIcon} {reason}",
    };

    private static string E(string? value) => (value ?? string.Empty)
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    private static string F(decimal? v) => v is { } value ? F(value) : "\u2014";

    private static string F(decimal v) => v.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string T(DateTimeOffset utc) => utc.ToOffset(MoscowClock.Offset).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
}