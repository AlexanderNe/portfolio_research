using PortfolioTradingSystem.Domain.Enums;
using PortfolioTradingSystem.Domain.Models;

namespace PortfolioTradingSystem.Domain.Strategy;

/// <summary>Signal created when a LONG or SHORT position is opened.</summary>
public sealed record TradeOpenedEvent(
    string Ticker,
    SignalDirection Direction,
    int Units,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TakeProfit,
    decimal AtrAtEntry,
    DateTimeOffset EntryTime);

/// <summary>Signal created when a position is closed.</summary>
public sealed record TradeClosedEvent(
    string Ticker,
    SignalDirection Direction,
    int Units,
    decimal EntryPrice,
    decimal ExitPrice,
    ExitReason Reason,
    decimal PnlRub,
    decimal ReturnPercent,
    decimal Commission,
    DateTimeOffset EntryTime,
    DateTimeOffset ExitTime);

/// <summary>Unrealized PnL of the open position marked at a price.</summary>
public readonly record struct OpenPositionPnl(decimal Rub, decimal Percent);

/// <summary>Point-in-time snapshot used for admin metrics / state view.</summary>
public sealed record EngineStateSnapshot(
    bool IsWarmedUp,
    decimal Cash,
    decimal Equity,
    decimal Atr,
    decimal RoC,
    int? PendingSignal,
    SignalDirection? PositionDirection,
    int PositionUnits,
    decimal? EntryPrice,
    decimal? StopLoss,
    decimal? TakeProfit,
    DateTimeOffset? EntryTime);

/// <summary>
/// Per-instrument daily momentum engine. A faithful, incremental port of the
/// research Simulator (strategy_research.py) with the research invariants:
///  * ATR and stop/take levels are computed from the PREVIOUS completed bar (no look-ahead).
///  * Entry happens at the next bar's OPEN when the prior bar's close produced a signal.
///  * SL is checked before TP when both are hit within one bar (conservative).
///  * At most one open position; sizing = risk% cash / sl-dist, capped by leverage.
///  * Commission applied on both sides of a trade; cash mechanics match the simulator.
/// Documented deviation from the research: intraday SL/TP are also checked
/// against each 1-minute candle after the entry candle, while the research only
/// checks them on subsequent daily bars. This is NOT merely "more conservative" -
/// it exits winners at the target earlier as well, so the live trade distribution
/// differs from the backtest by an amount that has not been measured.
/// </summary>
public sealed class MomentumEngineState
{
    private readonly StrategyOptions _o;
    private readonly string _ticker;
    private readonly int _lotSize;
    private readonly double _alpha;
    private readonly LinkedList<decimal> _closes = new();
    private decimal? _prevClose;
    private bool _firstBar = true;

    public MomentumEngineState(StrategyOptions options, string ticker, int lotSize = 1)
    {
        _o = options ?? throw new ArgumentNullException(nameof(options));
        _ticker = ticker;
        _lotSize = lotSize > 0 ? lotSize : 1;
        _alpha = 1.0 / options.AtrPeriod;
        Cash = options.InitialCapital;
    }

    public decimal Cash { get; private set; }

    /// <summary>Latest ATR value, computed from the last completed bar.</summary>
    public decimal Atr { get; private set; }

    /// <summary>ROC of the last completed bar: close/close[n] - 1.</summary>
    public decimal RoC { get; private set; }

    /// <summary>Signal (-1/0/+1) of the last completed bar; consumed on entry.</summary>
    public int? PendingSignal { get; private set; }

    public OpenPosition? Position { get; private set; }

    /// <summary>Enough closes collected to compute ROC (research: n+1 bars).</summary>
    public bool IsWarmedUp => _closes.Count >= _o.RocBars + 1;

    public decimal Equity(decimal lastPrice) =>
        Position is null
            ? Cash
            : Cash + Position.Units * lastPrice * (int)Position.Direction * _o.PointRub;

    /// <summary>Restore cash after a restart (initial capital + realized closed-trade PnL).</summary>
    public void SetCash(decimal cash) => Cash = cash;

    /// <summary>Restore an open position persisted before a restart.</summary>
    public void RestorePosition(OpenPosition position) => Position = position;

    /// <summary>Feed historical daily bars (warm-up). Bars must be ascending by time.</summary>
    public void WarmUp(IEnumerable<Candle> bars)
    {
        foreach (var bar in bars)
        {
            OnBarCompleted(bar);
        }
    }

    /// <summary>
    /// Finalize the completed session daily bar (updates ATR/ROC/signal).
    /// A bar flagged <see cref="Candle.IsComplete"/> = false (the engine only
    /// joined part-way through that session) still contributes its close to the
    /// ROC window but is kept out of ATR, whose true range would be understated
    /// by the missing part of the session.
    /// </summary>
    public void FinalizeDay(Candle completedBar) => OnBarCompleted(completedBar);

    public EngineStateSnapshot GetSnapshot(decimal lastPrice) => new(
        IsWarmedUp,
        Cash,
        Equity(lastPrice),
        Atr,
        RoC,
        PendingSignal,
        Position?.Direction,
        Position?.Units ?? 0,
        Position?.EntryPrice,
        Position?.StopLoss,
        Position?.TakeProfit,
        Position?.EntryTime);

    /// <summary>
    /// Open a position at the given open price using the pending signal from the
    /// previous bar (research: entry at bar i open when signal[i-1] != 0).
    /// Returns the created position or null when no entry is possible.
    /// </summary>
    public OpenPosition? TryOpen(decimal open, DateTimeOffset time)
    {
        if (Position is not null || !IsWarmedUp || PendingSignal is null || PendingSignal == 0 || Atr <= 0)
        {
            return null;
        }

        int dir = PendingSignal.Value;
        if (dir < 0 && !_o.ShortsEnabled)
        {
            PendingSignal = null;
            return null;
        }

        decimal notionalRub = open * _o.PointRub;
        if (notionalRub <= 0)
        {
            PendingSignal = null;
            return null;
        }

        decimal commissionRate = _o.CommissionPct / 100m;
        decimal atrAtEntry = Atr;
        decimal slDistRub = Math.Max(_o.SlAtr * atrAtEntry * _o.PointRub, open * _o.PointRub * (1e-6m));
        int units = (int)(_o.RiskPct / 100m * Cash / slDistRub);
        // the opening commission comes out of the same cash, so the cap has to
        // leave room for it or "notional <= cash" is breached by that commission.
        int unitsCap = (int)(_o.Leverage * Cash / (notionalRub * (1m + commissionRate)));
        units = Math.Max(0, Math.Min(units, unitsCap));
        // MOEX trades lots, not shares: a size that is not a whole number of lots
        // cannot be filled as advised.
        units -= units % _lotSize;
        if (units <= 0)
        {
            PendingSignal = null;
            return null;
        }

        decimal openCommission = units * open * commissionRate;
        decimal stopLoss, takeProfit;
        if (dir == 1)
        {
            stopLoss = open - _o.SlAtr * atrAtEntry;
            takeProfit = open + _o.TpAtr * atrAtEntry;
        }
        else
        {
            stopLoss = open + _o.SlAtr * atrAtEntry;
            takeProfit = open - _o.TpAtr * atrAtEntry;
        }

        if (dir == 1)
        {
            Cash -= units * open * _o.PointRub + openCommission;
        }
        else
        {
            Cash += units * open * _o.PointRub - openCommission;
        }

        Position = new OpenPosition
        {
            Ticker = _ticker,
            Direction = (SignalDirection)dir,
            Units = units,
            EntryPrice = open,
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            AtrAtEntry = atrAtEntry,
            OpenCommission = openCommission,
            EntryTime = time,
        };
        PendingSignal = null;
        return Position;
    }

    /// <summary>
    /// Check the position against a candle range. SL is checked before TP (research
    /// convention) and the fill is the SL/TP level, not the candle extreme - except
    /// when <paramref name="open"/> is supplied and the candle already opened beyond
    /// the level, in which case the level was never available and the fill is the
    /// open (stops gap through, targets gap into).
    /// </summary>
    public TradeClosedEvent? CheckStop(decimal high, decimal low, DateTimeOffset time, decimal? open = null)
    {
        var p = Position;
        if (p is null)
        {
            return null;
        }

        decimal? exit = null;
        ExitReason reason = ExitReason.StopLoss;
        if (p.Direction == SignalDirection.Long)
        {
            if (low <= p.StopLoss)
            {
                exit = open is { } o && o < p.StopLoss ? o : p.StopLoss;
                reason = ExitReason.StopLoss;
            }
            else if (high >= p.TakeProfit)
            {
                exit = open is { } o2 && o2 > p.TakeProfit ? o2 : p.TakeProfit;
                reason = ExitReason.TakeProfit;
            }
        }
        else
        {
            if (high >= p.StopLoss)
            {
                exit = open is { } o && o > p.StopLoss ? o : p.StopLoss;
                reason = ExitReason.StopLoss;
            }
            else if (low <= p.TakeProfit)
            {
                exit = open is { } o2 && o2 < p.TakeProfit ? o2 : p.TakeProfit;
                reason = ExitReason.TakeProfit;
            }
        }

        return exit is null ? null : ClosePosition(exit.Value, reason, time);
    }

    /// <summary>
    /// Research "signal_reversal": when the last completed bar's signal points
    /// OPPOSITE to the open position, close it at the given open so the engine
    /// can re-enter the new direction at the same open (python: close reason
    /// "signal_reversal" at the bar open - the one exception to
    /// "no entry on an exit bar", because both fills share the same open).
    /// The re-entry is a separate <see cref="TryOpen"/> call at the same price.
    /// Returns null when no reversal applies (no position / no opposite signal /
    /// shorts disabled). Mirrors the python gates: same signal, same entry
    /// conditions, same shorts rule.
    /// </summary>
    public TradeClosedEvent? TryCloseOnReversal(decimal open, DateTimeOffset time)
    {
        var p = Position;
        if (p is null || !IsWarmedUp || Atr <= 0 || open <= 0)
        {
            return null;
        }

        if (PendingSignal is not { } sig || sig == 0 || sig == (int)p.Direction)
        {
            return null;
        }

        if (sig < 0 && !_o.ShortsEnabled)
        {
            return null;
        }

        return ClosePosition(open, ExitReason.SignalReversal, time);
    }

    /// <summary>Close the open position at the given price (manual/system/intraday).</summary>
    public TradeClosedEvent ClosePosition(decimal price, ExitReason reason, DateTimeOffset time)
    {
        var p = Position ?? throw new InvalidOperationException($"No open position for {_ticker} to close.");
        var s = _o;
        decimal commission = s.CommissionPct / 100m;
        int dir = (int)p.Direction;
        decimal gross = (price - p.EntryPrice) * p.Units * dir * s.PointRub;
        decimal closeCommission = price * p.Units * commission;
        if (dir == 1)
        {
            Cash += price * p.Units * s.PointRub - closeCommission;
        }
        else
        {
            Cash -= price * p.Units * s.PointRub + closeCommission;
        }

        decimal pnl = gross - closeCommission - p.OpenCommission;
        decimal returnPct = pnl / (p.EntryPrice * p.Units * s.PointRub) * 100m;

        Position = null;
        return new TradeClosedEvent(
            p.Ticker, p.Direction, p.Units, p.EntryPrice, price, reason,
            pnl, returnPct, p.OpenCommission + closeCommission, p.EntryTime, time);
    }

    /// <summary>
    /// Unrealized PnL of the open position, marked at <paramref name="currentPrice"/>,
    /// using the same accounting as <see cref="ClosePosition"/>: gross minus the closing
    /// commission (estimated at the mark price) and the already-paid opening commission,
    /// matched against entry notional for the percent. Null when flat.
    /// </summary>
    public OpenPositionPnl? UnrealizedPnl(decimal currentPrice)
    {
        var p = Position;
        if (p is null)
        {
            return null;
        }

        int dir = (int)p.Direction;
        decimal commission = _o.CommissionPct / 100m;
        decimal gross = (currentPrice - p.EntryPrice) * p.Units * dir * _o.PointRub;
        decimal closeCommission = currentPrice * p.Units * commission;
        decimal rub = gross - closeCommission - p.OpenCommission;
        decimal baseNotional = p.EntryPrice * p.Units * _o.PointRub;
        return new OpenPositionPnl(rub, baseNotional > 0 ? rub / baseNotional * 100m : 0m);
    }

    private void OnBarCompleted(Candle bar)
    {
        if (!bar.IsComplete)
        {
            // Partial session: the close is real, the high/low/open are not.
        }
        else if (_firstBar)
        {
            // pandas EWM(adjust=False) seeds with the first true-range (high-low,
            // since there is no previous close yet).
            Atr = bar.High - bar.Low;
            _firstBar = false;
        }
        else
        {
            decimal tr = Math.Max(
                bar.High - bar.Low,
                Math.Max(Math.Abs(bar.High - _prevClose!.Value), Math.Abs(bar.Low - _prevClose!.Value)));
            Atr = (decimal)(_alpha * (double)tr + (1 - _alpha) * (double)Atr);
        }

        _prevClose = bar.Close;
        _closes.AddLast(bar.Close);
        while (_closes.Count > _o.RocBars + 1)
        {
            _closes.RemoveFirst();
        }

        if (_closes.Count >= _o.RocBars + 1)
        {
            decimal older = _closes.First!.Value;
            if (older == 0m)
            {
                return;
            }

            RoC = bar.Close / older - 1m;
            if (RoC > _o.RocThreshold)
            {
                PendingSignal = 1;
            }
            else if (RoC < -_o.RocThreshold)
            {
                PendingSignal = -1;
            }
            else
            {
                PendingSignal = 0;
            }
        }
    }
}