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
/// Live refinement (documented deviation): intraday SL/TP are also checked against
/// each 1-minute candle after the entry candle; the research only checks them on
/// subsequent daily bars. This makes same-day exits possible but is strictly more
/// risk-reducing and does not change the entry logic.
/// </summary>
public sealed class MomentumEngineState
{
    private readonly StrategyOptions _o;
    private readonly string _ticker;
    private readonly double _alpha;
    private readonly LinkedList<decimal> _closes = new();
    private decimal? _prevClose;
    private bool _firstBar = true;

    public MomentumEngineState(StrategyOptions options, string ticker)
    {
        _o = options ?? throw new ArgumentNullException(nameof(options));
        _ticker = ticker;
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

    /// <summary>Finalize the completed session daily bar (updates ATR/ROC/signal).</summary>
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

        decimal atrAtEntry = Atr;
        decimal slDistRub = Math.Max(_o.SlAtr * atrAtEntry * _o.PointRub, open * _o.PointRub * (1e-6m));
        int units = (int)(_o.RiskPct / 100m * Cash / slDistRub);
        decimal notionalRub = open * _o.PointRub;
        int unitsCap = (int)(_o.Leverage * Cash / notionalRub);
        units = Math.Max(0, Math.Min(units, unitsCap));
        if (units <= 0)
        {
            PendingSignal = null;
            return null;
        }

        decimal commission = _o.CommissionPct / 100m;
        decimal openCommission = units * open * commission;
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
    /// Check the position against an intraday candle range. SL checked before TP
    /// (research convention). Exits at the SL/TP level, not at the candle extreme.
    /// </summary>
    public TradeClosedEvent? CheckStop(decimal high, decimal low, DateTimeOffset time)
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
                exit = p.StopLoss;
                reason = ExitReason.StopLoss;
            }
            else if (high >= p.TakeProfit)
            {
                exit = p.TakeProfit;
                reason = ExitReason.TakeProfit;
            }
        }
        else
        {
            if (high >= p.StopLoss)
            {
                exit = p.StopLoss;
                reason = ExitReason.StopLoss;
            }
            else if (low <= p.TakeProfit)
            {
                exit = p.TakeProfit;
                reason = ExitReason.TakeProfit;
            }
        }

        return exit is null ? null : ClosePosition(exit.Value, reason, time);
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

    private void OnBarCompleted(Candle bar)
    {
        if (_firstBar)
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