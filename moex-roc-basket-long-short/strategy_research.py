"""
Strategy research backtester.

Goal: honestly compare several simple strategies on real historical data
with identical risk management (SL=2*ATR, TP=3*ATR, 0.05% commission per side,
session 10:00-23:30, force close 23:00, Mon-Fri only).

Methodology (to not fool ourselves):
  * data is split into train (first 70% by time) and test (last 30%);
  * parameters are selected ONLY on train (by best Sharpe);
  * out-of-sample metrics are read from test;
  * entry at the OPEN of the next bar (no look-ahead);
  * a bar that produced an exit can NOT also produce an entry: the position was
    still open at that bar's open, so re-entering there would be a fill at a
    price that no longer exists once the stop/target is hit;
  * an OPPOSITE signal while a position is open flips it at the NEXT bar's open:
    close the old leg at that open (reason "signal_reversal") and open the new
    direction at the same open - the same fill timing as a normal entry, so it is
    exempt from the "no entry on an exit bar" rule (both fills share the open);
  * a bar opening beyond a level fills at the OPEN (stops gap through);
  * if both SL and TP are hit in the same bar, SL wins (conservative).
"""

import logging
import sys
from datetime import datetime
from pathlib import Path
from typing import Dict, List

import numpy as np
import pandas as pd

logging.basicConfig(level=logging.WARNING, format="%(message)s")
logger = logging.getLogger("research")

SESSION_START = (10, 0)
SESSION_END = (23, 30)
FORCE_CLOSE_TIME = (23, 0)
TRADING_DAYS = (0, 1, 2, 3, 4)  # Mon-Fri

DEFAULT_COMMISSION_PCT = 0.05  # % на сторону
DEFAULT_SL_ATR = 2.0
DEFAULT_TP_ATR = 3.0
DEFAULT_ATR_PERIOD = 14
DEFAULT_INITIAL_CAPITAL = 100_000
MAX_DRAWDOWN_PERCENT = 10.0


# ---------------------------------------------------------------- data ----
def load_candle_data(file_path: Path) -> pd.DataFrame:
    df = pd.read_csv(file_path)
    df["timestamp"] = pd.to_datetime(df["timestamp"])
    # Приводим к наивному московскому времени
    if df["timestamp"].dt.tz is not None:
        df["timestamp"] = (
            df["timestamp"].dt.tz_convert("Europe/Moscow").dt.tz_localize(None)
        )
    df = df.sort_values("timestamp").reset_index(drop=True)
    return df


def in_session(ts) -> bool:
    t = ts.time()
    return (SESSION_START[0], SESSION_START[1]) <= (t.hour, t.minute) <= (
        SESSION_END[0], SESSION_END[1])


def should_force_close(ts) -> bool:
    t = ts.time()
    return (t.hour, t.minute) >= FORCE_CLOSE_TIME


def is_trading_day(ts) -> bool:
    return ts.weekday() in TRADING_DAYS


# --------------------------------------------------------- indicators ----
def compute_atr(df: pd.DataFrame, period: int) -> pd.Series:
    tr = pd.concat([
        df["high"] - df["low"],
        (df["high"] - df["close"].shift()).abs(),
        (df["low"] - df["close"].shift()).abs(),
    ], axis=1).max(axis=1)
    return tr.ewm(alpha=1 / period, adjust=False).mean()


# --------------------------------------------------------- simulator -----
class Simulator:
    def __init__(
        self,
        df: pd.DataFrame,
        initial_capital: float = DEFAULT_INITIAL_CAPITAL,
        commission_pct: float = DEFAULT_COMMISSION_PCT,
        sl_atr: float = DEFAULT_SL_ATR,
        tp_atr: float = DEFAULT_TP_ATR,
        atr_period: int = DEFAULT_ATR_PERIOD,
        session_gated: bool = False,
        eod_flat: bool = False,
        position_pct: float = 5.0,
        risk_pct: float = None,
        trail_atr: float = 0.0,
        open_range_n: int = 1,
        fixed_fee: float = 0.0,
        point_rub: float = 1.0,
        leverage: float = 1.0,
        fixed_units: int = 0,
        tp_frac: float = 0.0,
        sl_frac: float = 0.0,
        tp_pts: float = 0.0,
        sl_pts: float = 0.0,
        day_stop: float = 0.0,
        longs_only: bool = False,
        same_day_exit: bool = False,
        lot_size: int = 1,
        slippage_pct: float = 0.0,
        gap_fills: bool = True,
        atr_series=None,
    ):
        self.df = df
        self.initial_capital = initial_capital
        self.commission = commission_pct / 100.0
        self.fixed_fee = fixed_fee
        self.point_rub = point_rub
        self.leverage = leverage
        self.fixed_units = fixed_units
        self.tp_frac = tp_frac
        self.sl_frac = sl_frac
        self.tp_pts = tp_pts
        self.sl_pts = sl_pts
        self.day_stop = day_stop
        self.sl_atr = sl_atr
        self.tp_atr = tp_atr
        # ATR may be supplied pre-warmed (in a walk-forward fold the fold-local
        # EWM re-seeds from the fold's first bar and stays biased for roughly
        # 3*period bars, distorting both the stop distance and position sizing).
        self.atr = (compute_atr(df, atr_period).to_numpy()
                    if atr_series is None else np.asarray(atr_series, dtype=float))
        self.lot_size = max(1, int(lot_size))
        self.slippage = slippage_pct / 100.0
        self.gap_fills = gap_fills
        self.session_gated = session_gated
        self.eod_flat = eod_flat
        self.position_pct = position_pct
        self.risk_pct = risk_pct
        self.trail_atr = trail_atr
        self.open_range_n = open_range_n
        self.longs_only = longs_only
        self.same_day_exit = same_day_exit
        # Intraday day-range bounds for the tp_frac exit mode, computed once
        # instead of re-grouping the whole frame inside the per-bar loop.
        self._day_range = None
        if tp_frac > 0:
            dts = df["timestamp"].dt.date
            low_run = (df["low"].groupby(dts, sort=False).cummin()
                       .groupby(dts, sort=False).shift(1))
            high_run = (df["high"].groupby(dts, sort=False).cummax()
                        .groupby(dts, sort=False).shift(1))
            self._day_range = (high_run - low_run).to_numpy()

    def _fill(self, price: float, direction: int, entering: bool) -> float:
        """Apply adverse slippage to a fill price (0 disables)."""
        if self.slippage <= 0:
            return price
        return price * (1.0 + (direction if entering else -direction) * self.slippage)

    def run(self, signals: np.ndarray, initial_position=None,
            initial_cash: float = None, close_at_end: bool = True) -> Dict:
        """signals: +1/-1/0 array per bar (decision made on close of bar i).

        initial_position / initial_cash carry an open position (and the cash it
        already consumed) in from the previous walk-forward fold; close_at_end=False
        hands the still-open position back instead of force-closing it, so fold
        boundaries stop manufacturing artificial exits.
        """
        df = self.df
        atr = self.atr
        signals = np.asarray(signals, dtype=int)
        ts_list = df["timestamp"].tolist()
        op_arr = df["open"].to_numpy()
        hi_arr = df["high"].to_numpy()
        lo_arr = df["low"].to_numpy()
        cl_arr = df["close"].to_numpy()
        n_bars = len(df)

        cash = self.initial_capital if initial_cash is None else initial_cash
        position = initial_position  # {dir, units, entry, sl, tp, entry_time}
        trades = []
        peak = self.initial_capital
        max_dd = 0.0
        daily = {}  # close income per day

        # --- per-bar gating ---
        if self.eod_flat:
            dates = [t.date() for t in ts_list]
            last_of_day = [dates[i] != dates[i + 1] for i in range(n_bars - 1)] + [True]
            can_enter = [not last_of_day[i] for i in range(n_bars)]
            force_bar = last_of_day
        elif self.session_gated:
            can_enter = [
                is_trading_day(ts_list[i]) and in_session(ts_list[i])
                and not should_force_close(ts_list[i])
                for i in range(n_bars)
            ]
            force_bar = [
                should_force_close(ts_list[i]) or not in_session(ts_list[i])
                for i in range(n_bars)
            ]
        elif self.same_day_exit:
            can_enter = [True] * n_bars
            force_bar = [False] * n_bars
        else:
            can_enter = [True] * n_bars
            force_bar = [False] * n_bars

        cur_date = None
        day_cum = 0.0
        halt_date = None

        for i in range(n_bars):
            exited_this_bar = False
            reversal_exit = False
            ts = ts_list[i]
            op = op_arr[i]
            hi = hi_arr[i]
            lo = lo_arr[i]
            close = cl_arr[i]

            # --- per-day loss lockout: fresh equity each day ---
            if ts.date() != cur_date:
                cur_date = ts.date()
                day_cum = 0.0
                halt_date = None

            # --- manage open position on this bar ---
            if position is not None:
                exit_price = None
                reason = None
                # Signal reversal: the previous bar's close signalled the OPPOSITE
                # direction to the open position. Close at THIS bar's open (the
                # entry-reverse block below will re-enter the new direction at the
                # same open, keeping fill timing identical to a normal entry).
                if (i > 0 and signals[i - 1] != 0
                        and signals[i - 1] != position["dir"]
                        and can_enter[i]
                        and (signals[i - 1] > 0 or not self.longs_only)
                        and (halt_date is None or ts.date() != halt_date)):
                    exit_price = op
                    reason = "signal_reversal"
                    reversal_exit = True
                if reason is None and self.trail_atr > 0:
                    a_e = position["atr"]
                    if position["dir"] == 1:
                        if hi > position["best"]:
                            position["best"] = hi
                        stop = max(position["best"] - self.trail_atr * a_e, position["sl"])
                        if lo <= stop:
                            exit_price, reason = stop, "trail_stop"
                    else:
                        if lo < position["best"]:
                            position["best"] = lo
                        stop = min(position["best"] + self.trail_atr * a_e, position["sl"])
                        if hi >= stop:
                            exit_price, reason = stop, "trail_stop"
                elif reason is None:
                    # A bar that opens beyond the level fills at the OPEN, not at
                    # the level: stops get gapped through, targets gapped into.
                    if position["dir"] == 1:
                        if lo <= position["sl"]:
                            exit_price = (min(op, position["sl"]) if self.gap_fills
                                          else position["sl"])
                            reason = "stop_loss"
                        elif position["tp"] is not None and hi >= position["tp"]:
                            exit_price = (max(op, position["tp"]) if self.gap_fills
                                          else position["tp"])
                            reason = "take_profit"
                    else:
                        if hi >= position["sl"]:
                            exit_price = (max(op, position["sl"]) if self.gap_fills
                                          else position["sl"])
                            reason = "stop_loss"
                        elif position["tp"] is not None and lo <= position["tp"]:
                            exit_price = (min(op, position["tp"]) if self.gap_fills
                                          else position["tp"])
                            reason = "take_profit"

                if exit_price is None and force_bar[i]:
                    exit_price, reason = close, "end_of_day"

                if exit_price is not None:
                    exit_price = self._fill(exit_price, position["dir"], False)
                    gross = (exit_price - position["entry"]) * position["units"] * position["dir"] * self.point_rub
                    comm_close = exit_price * position["units"] * self.commission + self.fixed_fee
                    if position["dir"] == 1:
                        cash += exit_price * position["units"] * self.point_rub - comm_close
                    else:
                        cash -= exit_price * position["units"] * self.point_rub + comm_close
                    pnl = gross - comm_close - position["open_comm"]
                    day_cum += pnl
                    if self.day_stop > 0 and day_cum <= -self.day_stop:
                        halt_date = ts.date()
                    trades.append({
                        "entry_time": position["entry_time"],
                        "exit_time": ts,
                        "dir": position["dir"],
                        "units": position["units"],
                        "entry": position["entry"],
                        "exit": exit_price,
                        "reason": reason,
                        "pnl": pnl,
                        "ret": pnl / (position["entry"] * position["units"] * self.point_rub),
                    })
                    position = None
                    exited_this_bar = True

            # --- entry on prior bar's signal ---
            # NEVER on a bar that already produced an exit: the position was still
            # open at this bar's open, so re-entering at that open is a fill at a
            # price that no longer exists by the time the stop/target is hit.
            # EXCEPT a signal reversal, whose exit and entry both fill at the open.
            if (position is None and (not exited_this_bar or reversal_exit)
                    and i > 0 and signals[i - 1] != 0
                    and can_enter[i]
                    and (halt_date is None or ts.date() != halt_date)):
                dir_sig = signals[i - 1]
                if dir_sig < 0 and self.longs_only:
                    dir_sig = 0
                if atr[i - 1] > 0:
                    a = atr[i - 1]
                    entry = self._fill(op, dir_sig, True)
                    if self.fixed_units > 0:
                        units = self.fixed_units
                    elif self.risk_pct is not None:
                        sl_dist_rub = max(self.sl_atr * a * self.point_rub,
                                          entry * self.point_rub * 1e-6)
                        units = int((self.risk_pct / 100.0 * cash) / sl_dist_rub)
                    else:
                        units = int((self.position_pct / 100.0 * cash) /
                                    (entry * self.point_rub))
                    notional_rub = entry * self.point_rub
                    # the opening commission is paid out of the same cash, so the
                    # cap has to leave room for it - otherwise "notional <= cash"
                    # is breached by exactly the commission.
                    units_cap = int(self.leverage * cash /
                                    (notional_rub * (1.0 + self.commission)))
                    units = max(0, min(units, units_cap))
                    if self.lot_size > 1:
                        units = (units // self.lot_size) * self.lot_size
                    if units > 0:
                        open_comm = units * entry * self.commission + self.fixed_fee
                        if dir_sig == 1:
                            cash -= units * entry * self.point_rub + open_comm
                        else:
                            cash += units * entry * self.point_rub - open_comm
                        if self.tp_pts > 0:
                            if dir_sig == 1:
                                sl, tp = entry - self.sl_pts, entry + self.tp_pts
                            else:
                                sl, tp = entry + self.sl_pts, entry - self.tp_pts
                        elif self.tp_frac > 0:
                            rng = max(self._day_range[i], a)
                            slf = self.sl_frac if self.sl_frac > 0 else 1.0
                            if dir_sig == 1:
                                sl, tp = entry - slf * rng, entry + self.tp_frac * rng
                            else:
                                sl, tp = entry + slf * rng, entry - self.tp_frac * rng
                        elif dir_sig == 1:
                            sl, tp = entry - self.sl_atr * a, entry + self.tp_atr * a
                        else:
                            sl, tp = entry + self.sl_atr * a, entry - self.tp_atr * a
                        # store position metadata (trail needs ATR-at-entry and best price)
                        sl_, tp_ = sl, tp
                        if self.trail_atr > 0 and self.tp_frac == 0 and self.tp_pts == 0:
                            tp_ = None
                        position = {
                            "dir": dir_sig, "units": units, "entry": entry,
                            "sl": sl_, "tp": tp_, "entry_time": ts,
                            "open_comm": open_comm,
                            "atr": a, "best": entry,
                        }

            # --- no-overnight mode: close any position opened this bar by the
            # end of this bar (SL/TP against today's range first, else close).
            if self.same_day_exit and position is not None \
                    and position["entry_time"] == ts:
                d_s = position["dir"]
                sl_v, tp_v = position["sl"], position["tp"]
                exit_price, reason = None, None
                if tp_v is not None:
                    if d_s == 1:
                        if lo <= sl_v:
                            exit_price, reason = sl_v, "stop_loss"
                        elif hi >= tp_v:
                            exit_price, reason = tp_v, "take_profit"
                    else:
                        if hi >= sl_v:
                            exit_price, reason = sl_v, "stop_loss"
                        elif lo <= tp_v:
                            exit_price, reason = tp_v, "take_profit"
                if exit_price is None:
                    exit_price, reason = close, "end_of_day"
                exit_price = self._fill(exit_price, d_s, False)
                gross = (exit_price - position["entry"]) * position["units"] * position["dir"] * self.point_rub
                comm_close = exit_price * position["units"] * self.commission + self.fixed_fee
                if position["dir"] == 1:
                    cash += exit_price * position["units"] * self.point_rub - comm_close
                else:
                    cash -= exit_price * position["units"] * self.point_rub + comm_close
                pnl = gross - comm_close - position["open_comm"]
                day_cum += pnl
                if self.day_stop > 0 and day_cum <= -self.day_stop:
                    halt_date = ts.date()
                trades.append({
                    "entry_time": position["entry_time"], "exit_time": ts,
                    "dir": position["dir"], "units": position["units"],
                    "entry": position["entry"], "exit": exit_price,
                    "reason": reason, "pnl": pnl,
                    "ret": pnl / (position["entry"] * position["units"] * self.point_rub),
                })
                position = None

            # --- equity point ---
            equity = cash
            if position is not None:
                equity += position["units"] * close * position["dir"] * self.point_rub
            if equity > peak:
                peak = equity
            dd = (peak - equity) / peak * 100 if peak > 0 else 0
            if dd > max_dd:
                max_dd = dd
            daily.setdefault(ts.date(), []).append(equity)

        # Close whatever is left only when asked to. A walk-forward fold that
        # hands its position to the next fold must NOT book an artificial
        # "backtest_end" trade at the fold boundary.
        if position is not None and close_at_end:
            close = self._fill(cl_arr[-1], position["dir"], False)
            gross = (close - position["entry"]) * position["units"] * position["dir"] * self.point_rub
            comm_close = close * position["units"] * self.commission + self.fixed_fee
            if position["dir"] == 1:
                cash += close * position["units"] * self.point_rub - comm_close
            else:
                cash -= close * position["units"] * self.point_rub + comm_close
            trades.append({
                "entry_time": position["entry_time"],
                "exit_time": ts_list[-1],
                "dir": position["dir"],
                "units": position["units"],
                "entry": position["entry"],
                "exit": close,
                "reason": "backtest_end",
                "pnl": gross - comm_close - position["open_comm"],
                "ret": (gross - comm_close - position["open_comm"]) / (position["entry"] * position["units"] * self.point_rub),
            })
            position = None

        total_pnl = sum(t["pnl"] for t in trades)
        daily_close = pd.Series([v[-1] for v in daily.values()])
        if len(daily_close) > 2:
            rets = daily_close.pct_change().dropna()
            sharpe = (rets.mean() / rets.std() * np.sqrt(252)) if rets.std() > 0 else 0.0
        else:
            sharpe = 0.0

        wins = [t for t in trades if t["pnl"] > 0]
        return {
            "trades": trades,
            "n_trades": len(trades),
            "total_pnl": total_pnl,
            "total_return": total_pnl / self.initial_capital * 100,
            "win_rate": len(wins) / len(trades) * 100 if trades else 0.0,
            "avg_ret_per_trade": (np.mean([t["ret"] for t in trades]) * 100) if trades else 0.0,
            "sharpe": sharpe,
            "max_drawdown": max_dd,
            "equity_curve": [(d, v[-1]) for d, v in daily.items()],
            "open_position": position,
            "cash": cash,
        }


# ------------------------------------------------------ strategies -------
def strat_donchian(df: pd.DataFrame, n: int) -> np.ndarray:
    roll_high = df["high"].rolling(n).max().shift(1)
    roll_low = df["low"].rolling(n).min().shift(1)
    sig = np.empty(len(df), dtype=int)
    sig[:] = 0
    sig[df["close"] > roll_high] = 1
    sig[df["close"] < roll_low] = -1
    return sig


def strat_trend_breakout(df: pd.DataFrame, n: int, ema_span: int) -> np.ndarray:
    """Donchian breakout filtered by EMA regime: long only above EMA, short only below."""
    roll_high = df["high"].rolling(n).max().shift(1)
    roll_low = df["low"].rolling(n).min().shift(1)
    ema = df["close"].ewm(span=ema_span, adjust=False).mean()
    close = df["close"]
    sig = np.zeros(len(df), dtype=int)
    sig[(close > roll_high) & (close > ema)] = 1
    sig[(close < roll_low) & (close < ema)] = -1
    return sig


def strat_bb_reversion(df: pd.DataFrame, period: int, k: float) -> np.ndarray:
    mean = df["close"].rolling(period).mean()
    std = df["close"].rolling(period).std()
    upper = mean + k * std
    lower = mean - k * std
    sig = np.empty(len(df), dtype=int)
    sig[:] = 0
    sig[df["close"] < lower] = 1
    sig[df["close"] > upper] = -1
    return sig


def strat_ema_cross(df: pd.DataFrame, fast: int, slow: int) -> np.ndarray:
    fast_ema = df["close"].ewm(span=fast, adjust=False).mean()
    slow_ema = df["close"].ewm(span=slow, adjust=False).mean()
    sig = np.empty(len(df), dtype=int)
    sig[:] = 0
    sig[fast_ema > slow_ema] = 1
    sig[fast_ema < slow_ema] = -1
    return sig


def strat_rsi_reversion(df: pd.DataFrame, period: int, level: float) -> np.ndarray:
    diff = df["close"].diff()
    gain = diff.clip(lower=0)
    loss = (-diff).clip(lower=0)
    avg_gain = gain.ewm(alpha=1 / period, adjust=False).mean()
    avg_loss = loss.ewm(alpha=1 / period, adjust=False).mean()
    rs = avg_gain / avg_loss.replace(0, np.nan)
    rsi = (100 - 100 / (1 + rs)).fillna(50)
    sig = np.empty(len(df), dtype=int)
    sig[:] = 0
    sig[rsi < level] = 1
    sig[rsi > 100 - level] = -1
    return sig


def strat_roc_momentum(df: pd.DataFrame, n: int, threshold: float) -> np.ndarray:
    roc = df["close"].pct_change(n)
    sig = np.empty(len(df), dtype=int)
    sig[:] = 0
    sig[roc > threshold] = 1
    sig[roc < -threshold] = -1
    return sig


def strat_orbb(df: pd.DataFrame, n: int) -> np.ndarray:
    """Opening-range breakout: range high/low taken from the first n bars of each day.
    First breakout bar of a day fires a signal (+1 long above range high, -1 short below range low)."""
    hi = df["high"].to_numpy(dtype=float)
    lo = df["low"].to_numpy(dtype=float)
    n_bars = len(df)
    sig = np.zeros(n_bars, dtype=int)
    by_day = {}
    for i in range(n_bars):
        by_day.setdefault(df["timestamp"].iloc[i].date(), []).append(i)
    for inds in by_day.values():
        rh = float("-inf")
        rl = float("inf")
        for j in inds[:n]:
            rh = max(rh, hi[j])
            rl = min(rl, lo[j])
        acted = False
        for j in inds[n:]:
            if acted:
                sig[j] = 0
                continue
            if hi[j] > rh:
                sig[j] = 1
                acted = True
            elif lo[j] < rl:
                sig[j] = -1
                acted = True
            else:
                sig[j] = 0
    return sig


def strat_range_reversion(df: pd.DataFrame, n: int, thr: float) -> np.ndarray:
    """Mean-reversion in a range: go long when close is inside the bottom `thr`
    zone of the trailing n-bar range, short when in the top zone. Fades extremes."""
    roll_high = df["high"].rolling(n).max().shift(1)
    roll_low = df["low"].rolling(n).min().shift(1)
    span = roll_high - roll_low
    ok = span > (roll_high.abs() * 1e-9 + 1e-12)
    zone_low = roll_low + thr * span
    zone_high = roll_high - thr * span
    sig = np.zeros(len(df), dtype=int)
    sig[ok & (df["close"] <= zone_low)] = 1
    sig[ok & (df["close"] >= zone_high)] = -1
    return sig


def strat_day_range(df: pd.DataFrame, thr: float,
                    trade_after_hour: float | None = None,
                    trend_days: int = 5, trend_thr: float = 0.0,
                    fresh_look: int = 1) -> np.ndarray:
    """Fade the intraday range: long when close is near the running day low
    (day min so far), short when close is near the running day high (day max so far).
    Day min/max look back only up to the previous bar (no lookahead).
    If trade_after_hour is set (e.g. 13.0), the first part of the day is a pure
    observation window that tracks day min/max but emits no signals.
    Fresh-extreme gating: a signal fires only if the running extreme was just
    extended within the last `fresh_look` bars, so each trade corresponds to a
    brand-new day extreme (no re-fading the same price).
    Trend filter over the last `trend_days` sessions: the session day's trend is
    the sign of (last close / close `trend_days` sessions earlier - 1) vs +-trend_thr.
    Bullish -> longs only, bearish -> shorts only, neutral/undefined -> both."""
    day = df["timestamp"].dt.date
    low_cum = df["low"].groupby(day, sort=False).cummin()
    high_cum = df["high"].groupby(day, sort=False).cummax()
    low_run = low_cum.groupby(day, sort=False).shift(1)
    high_run = high_cum.groupby(day, sort=False).shift(1)
    new_low = (low_run != low_run.shift(1)).fillna(False)
    new_high = (high_run != high_run.shift(1)).fillna(False)
    fresh_low = new_low.rolling(fresh_look, min_periods=1).max().astype(bool)
    fresh_high = new_high.rolling(fresh_look, min_periods=1).max().astype(bool)
    day_close = df.groupby(day, sort=False)["close"].last()
    roc = day_close / day_close.shift(trend_days) - 1
    state = pd.Series(0, index=roc.index)
    state[roc > trend_thr] = 1
    state[roc < -trend_thr] = -1
    trend = day.map(state).fillna(0.0)
    sig = np.zeros(len(df), dtype=int)
    near_low = df["close"] <= low_run * (1.0 + thr)
    near_high = df["close"] >= high_run * (1.0 - thr)
    if trade_after_hour is not None:
        t = df["timestamp"].dt.hour * 60 + df["timestamp"].dt.minute
        gate = t >= trade_after_hour * 60
        near_low &= gate
        near_high &= gate
    sig[near_low & fresh_low.to_numpy() & (trend.to_numpy() != -1)] = 1
    sig[near_high & fresh_high.to_numpy() & (trend.to_numpy() != 1)] = -1
    return sig


# ------------------------------------------------------------- research ---
def strat_vwap_reversion(df: pd.DataFrame, k: float, atr_n: int = 20,
                         min_hour: float = 10.5, max_hour: float = 18.3) -> np.ndarray:
    """Fade deviations from the day's anchored VWAP (computed up to the previous
    bar, no lookahead). Long when price makes a FRESH touch below
    vwap - k*ATR, short on a fresh touch above vwap + k*ATR.
    Only trades between min_hour and max_hour (open/close chaos avoided)."""
    ts = df["timestamp"]
    day = ts.dt.date
    typical = (df["high"] + df["low"] + df["close"]) / 3.0
    vol = df["volume"]
    cpv = (typical * vol).groupby(day, sort=False).cumsum()
    cv = vol.groupby(day, sort=False).cumsum()
    vwap = (cpv / cv.replace(0, np.nan)).groupby(day, sort=False).shift(1)
    prev_c = df["close"].shift(1)
    tr = pd.concat([
        df["high"] - df["low"],
        (df["high"] - prev_c).abs(),
        (df["low"] - prev_c).abs(),
    ], axis=1).max(axis=1)
    atr = tr.rolling(atr_n).mean().shift(1)
    up = vwap + k * atr
    dn = vwap - k * atr
    low = df["low"]
    high = df["high"]
    prev_low = low.shift(1)
    prev_high = high.shift(1)
    prev_up = up.shift(1)
    prev_dn = dn.shift(1)
    touch_dn = (low <= dn) & (prev_low > prev_dn)
    touch_up = (high >= up) & (prev_high < prev_up)
    t = ts.dt.hour * 60 + ts.dt.minute
    off = (t >= min_hour * 60) & (t <= max_hour * 60 - 1)
    sig = np.zeros(len(df), dtype=int)
    sig[touch_dn.to_numpy() & off.to_numpy()] = 1
    sig[touch_up.to_numpy() & off.to_numpy()] = -1
    return sig


def strat_intraday_momentum(df: pd.DataFrame, boost: float, atr_n: int = 20,
                            strong_close: bool = False,
                            min_hour: float = 10.0, max_hour: float = 18.8,
                            trend_days: int = 5, trend_thr: float = 0.0,
                            vwap_dev: float = 0.0) -> np.ndarray:
    """Intraday trend-following: buy when close makes a new running day high by a
    boost-multiple of ATR over the previous day extreme; sell on new day lows.
    The inverse of the day-range fade; only trades confirmation, not anticipation.
    Optional filters to raise trade quality:
      strong_close - long only on bullish candles (close > open), short only bearish
      min_hour/max_hour - trade window in hours (avoid open/close chaos if desired)
      trend_days/trend_thr - higher-timeframe filter (5-session ROC sign); longs
        suppressed on bearish state, shorts suppressed on bullish state
      vwap_dev - flow/regime gate in ATR units: require price to already be that far
        from the day's anchored VWAP (i.e., the market is TRENDING, not chopping);
        0.0 disables the gate"""
    day = df["timestamp"].dt.date
    hi = df["high"]
    lo = df["low"]
    op = df["open"]
    cl = df["close"]
    high_run = hi.groupby(day, sort=False).cummax().groupby(day, sort=False).shift(1)
    low_run = lo.groupby(day, sort=False).cummin().groupby(day, sort=False).shift(1)
    prev_c = cl.shift(1)
    tr = pd.concat([
        hi - lo,
        (hi - prev_c).abs(),
        (lo - prev_c).abs(),
    ], axis=1).max(axis=1)
    atr = tr.rolling(atr_n).mean().shift(1)
    sig = np.zeros(len(df), dtype=int)
    upper = high_run + boost * atr
    lower = low_run - boost * atr
    long_ok = cl > upper
    short_ok = cl < lower
    if strong_close:
        long_ok &= cl > op
        short_ok &= cl < op
    if vwap_dev > 0:
        typical = (hi + lo + cl) / 3.0
        vol = df["volume"]
        cpv = (typical * vol).groupby(day, sort=False).cumsum()
        cv = vol.groupby(day, sort=False).cumsum()
        vwap = (cpv / cv.replace(0, np.nan)).groupby(day, sort=False).shift(1)
        long_ok &= (cl - vwap) > vwap_dev * atr
        short_ok &= (vwap - cl) > vwap_dev * atr
    t = df["timestamp"].dt.hour * 60 + df["timestamp"].dt.minute
    gate = (t >= min_hour * 60) & (t <= max_hour * 60 - 1)
    long_ok &= gate.to_numpy()
    short_ok &= gate.to_numpy()
    if trend_days:
        day_close = df.groupby(day, sort=False)["close"].last()
        roc = day_close / day_close.shift(trend_days) - 1
        state = pd.Series(0, index=roc.index)
        state[roc > trend_thr] = 1
        state[roc < -trend_thr] = -1
        trend = day.map(state).fillna(0.0).to_numpy()
        long_ok &= trend != -1
        short_ok &= trend != 1
    sig[long_ok.to_numpy()] = 1
    sig[short_ok.to_numpy()] = -1
    return sig


def strategy_grids(scale: str = "bar") -> Dict[str, List[Dict]]:
    if scale == "min":  # parameter ranges sized for 1-minute bars
        return {
            "donchian_breakout": [{"n": n} for n in (15, 30, 60, 120, 240)],
            "trend_breakout": [
                {"n": n, "ema": e}
                for n in (30, 60, 120)
                for e in (500, 1000)
            ],
            "bb_reversion": [
                {"period": p, "k": k}
                for p in (15, 30, 60)
                for k in (1.5, 2.0, 2.5)
            ],
            "ema_cross": [
                {"fast": f, "slow": s}
                for f in (5, 10, 20)
                for s in (50, 100, 200)
                if f < s
            ],
            "rsi_reversion": [
                {"period": p, "level": lvl}
                for p in (2, 5, 14)
                for lvl in (20, 30, 40)
            ],
            "roc_momentum": [
                {"n": n, "thr": thr}
                for n in (15, 30, 60, 120)
                for thr in (0.0005, 0.001, 0.002)
            ],
            "orbb": [{"n": 1}],
            "range_reversion": [
                {"n": n, "thr": thr}
                for n in (30, 60, 120)
                for thr in (0.10, 0.20, 0.30, 0.40)
            ],
            "day_range": [
                {"thr": thr, "trade_after_hour": 13.0,
                 "trend_days": 5, "trend_thr": tt,
                 "fresh_look": look}
                for thr in (0.001, 0.002, 0.005, 0.01)
                for tt in (0.0, 0.005, 0.01, 0.02)
                for look in (1, 2)
            ],
            "vwap_reversion": [{"k": k} for k in (1.0, 1.5, 2.0, 2.5)],
            "intraday_momentum": [
                {"boost": b, "atr_n": 20, "strong_close": 0,
                 "min_hour": mi, "max_hour": 18.8,
                 "trend_days": 5, "trend_thr": tt, "vwap_dev": 0.0}
                for b in (1.0, 2.0)
                for mi in (10.0, 11.0)
                for tt in (0.0, 0.01)
            ],
        }
    return {
        "donchian_breakout": [{"n": n} for n in (10, 20, 40, 80)],
        "trend_breakout": [
            {"n": n, "ema": e}
            for n in (20, 40, 80)
            for e in (200, 400)
        ],
        "bb_reversion": [
            {"period": p, "k": k}
            for p in (10, 20, 40)
            for k in (1.5, 2.0, 2.5)
        ],
        "ema_cross": [
            {"fast": f, "slow": s}
            for f in (5, 10, 20)
            for s in (50, 100, 200)
            if f < s
        ],
        "rsi_reversion": [
            {"period": p, "level": lvl}
            for p in (2, 5, 14)
            for lvl in (20, 30, 40)
        ],
        "roc_momentum": [
            {"n": n, "thr": thr}
            for n in (5, 10, 20)
            for thr in (0.01, 0.02, 0.04)
        ],
        "orbb": [{"n": 1}],
        "range_reversion": [
            {"n": n, "thr": thr}
            for n in (10, 20, 40)
            for thr in (0.10, 0.20, 0.30, 0.40)
        ],
        "day_range": [
            {"thr": thr, "trade_after_hour": 13.0}
            for thr in (0.001, 0.002, 0.005, 0.01)
        ],
        "vwap_reversion": [{"k": k} for k in (1.5, 2.5)],
        "intraday_momentum": [{"boost": b} for b in (0.5, 1.0)],
    }


TRAIL_GRID = (0.0, 2.0, 3.0)  # 0 = fixed SL/TP, >0 = trailing stop in ATR units


STRAT_FUNCS = {
    "donchian_breakout": lambda d, p: strat_donchian(d, p["n"]),
    "trend_breakout": lambda d, p: strat_trend_breakout(d, p["n"], p["ema"]),
    "bb_reversion": lambda d, p: strat_bb_reversion(d, p["period"], p["k"]),
    "ema_cross": lambda d, p: strat_ema_cross(d, p["fast"], p["slow"]),
    "rsi_reversion": lambda d, p: strat_rsi_reversion(d, p["period"], p["level"]),
    "roc_momentum": lambda d, p: strat_roc_momentum(d, p["n"], p["thr"]),
    "orbb": lambda d, p: strat_orbb(d, p["n"]),
    "range_reversion": lambda d, p: strat_range_reversion(d, p["n"], p["thr"]),
    "day_range": lambda d, p: strat_day_range(d, p["thr"],
                                 p.get("trade_after_hour"),
                                 p.get("trend_days", 5),
                                 p.get("trend_thr", 0.0),
                                 p.get("fresh_look", 1)),
    "vwap_reversion": lambda d, p: strat_vwap_reversion(d, p["k"]),
    "intraday_momentum": lambda d, p: strat_intraday_momentum(
        d, p["boost"], p.get("atr_n", 20), p.get("strong_close", False),
        p.get("min_hour", 10.0), p.get("max_hour", 18.8),
        p.get("trend_days", 5), p.get("trend_thr", 0.0),
        p.get("vwap_dev", 0.0)),
}


def run_instrument(file_path: Path, intraday: bool, cfg: Dict,
                   grids: Dict = None, min_trades: int = 15) -> Dict:
    df = load_candle_data(file_path)
    ticker = file_path.stem
    if len(df) < 1000 and intraday:
        return {"ticker": ticker, "skipped": True, "reason": "too few bars"}
    if not intraday and len(df) < 200:
        return {"ticker": ticker, "skipped": True, "reason": "too few bars"}

    split = int(len(df) * 0.7)
    train, test = df.iloc[:split].reset_index(drop=True), df.iloc[split:].reset_index(drop=True)
    eod_flat = intraday
    risk_pct = cfg["risk_pct"]
    sl_atr = cfg["sl_atr"]
    tp_atr = cfg["tp_atr"]
    commission = cfg["commission"]
    fixed_fee = cfg.get("fixed_fee", 0.0)
    point_rub = cfg.get("point_rub", 1.0)
    leverage = cfg.get("leverage", 1.0)
    fixed_units = cfg.get("fixed_units", 0)
    trails = cfg["trail_grid"]
    sell_fee = fixed_fee if cfg.get("fee_selection", False) else 0.0
    sell_comm = commission if cfg.get("fee_selection", False) else 0.0

    results = {"ticker": ticker, "intraday": intraday, "strategies": {}}

    import time as _time
    _t0 = _time.time()
    lot_size = cfg.get("lot_size", 1)
    slippage = cfg.get("slippage_pct", 0.0)
    if grids is None:
        grids = strategy_grids()
    strat_tot = len(grids)
    for si, (name, grid) in enumerate(grids.items(), 1):
        print(f"  [{ticker}] {name}: training grid "
              f"({len(grid) * len(trails)} combos) ...", flush=True)
        best, best_score = None, -np.inf
        exits = cfg.get("exit_grid", [(sl_atr, tp_atr)])
        specs = [("atr", sl_a, tp_a) for sl_a, tp_a in exits]
        # see walk_forward: spec (a, b) is (sl, tp), the CLI pairs are (tp, sl)
        specs += [("range", sl_f, tp_f) for tp_f, sl_f in cfg.get("range_targets", [])]
        specs += [("pts", sl_p, tp_p) for sl_p, tp_p in cfg.get("pt_targets", [])]
        for pi, params in enumerate(grid, 1):
            sig = STRAT_FUNCS[name](train, params)
            for kind, a, b in specs:
                for trail in trails:
                    sim = Simulator(
                        train, eod_flat=eod_flat, risk_pct=risk_pct,
                        sl_atr=a if kind == "atr" else sl_atr,
                        tp_atr=b if kind == "atr" else tp_atr,
                        commission_pct=sell_comm,
                        trail_atr=trail if kind == "atr" else 0.0,
                        tp_frac=b if kind == "range" else 0.0,
                        sl_frac=a if kind == "range" else 0.0,
                        tp_pts=b if kind == "pts" else 0.0,
                        sl_pts=a if kind == "pts" else 0.0,
                        fixed_fee=sell_fee, point_rub=point_rub,
                        leverage=leverage, fixed_units=fixed_units,
                        day_stop=cfg.get("day_stop", 0.0),
                        lot_size=lot_size, slippage_pct=slippage,
                        longs_only=cfg.get("longs_only", False),
                        same_day_exit=cfg.get("same_day_exit", False))
                    out = sim.run(sig)
                    if out["n_trades"] >= min_trades:
                        score = out["sharpe"]
                        if score > best_score:
                            best_score, best = score, (params, kind, a, b, trail)
            if pi % 3 == 0 or pi == len(grid):
                print(f"    {name}: {pi}/{len(grid)} param sets done "
                      f"({_time.time() - _t0:.0f}s)", flush=True)
        print(f"  [{ticker}] {name}: done in {_time.time() - _t0:.0f}s, "
              f"({si}/{strat_tot} strategies)", flush=True)

        if best is None:
            results["strategies"][name] = {"chosen": None, "skipped": True}
            continue

        params, kind, a, b, trail = best
        sig_test = STRAT_FUNCS[name](test, params)
        test_out = Simulator(
            test, eod_flat=eod_flat, risk_pct=risk_pct,
            sl_atr=a if kind == "atr" else sl_atr,
            tp_atr=b if kind == "atr" else tp_atr,
            commission_pct=commission,
            trail_atr=trail if kind == "atr" else 0.0,
            tp_frac=b if kind == "range" else 0.0,
            sl_frac=a if kind == "range" else 0.0,
            tp_pts=b if kind == "pts" else 0.0,
            sl_pts=a if kind == "pts" else 0.0,
            fixed_fee=fixed_fee, point_rub=point_rub,
            leverage=leverage, fixed_units=fixed_units,
            day_stop=cfg.get("day_stop", 0.0),
            lot_size=lot_size, slippage_pct=slippage,
            longs_only=cfg.get("longs_only", False),
            same_day_exit=cfg.get("same_day_exit", False)).run(sig_test)
        train_out = Simulator(
            train, eod_flat=eod_flat, risk_pct=risk_pct,
            sl_atr=a if kind == "atr" else sl_atr,
            tp_atr=b if kind == "atr" else tp_atr,
            commission_pct=commission,
            trail_atr=trail if kind == "atr" else 0.0,
            tp_frac=b if kind == "range" else 0.0,
            sl_frac=a if kind == "range" else 0.0,
            tp_pts=b if kind == "pts" else 0.0,
            sl_pts=a if kind == "pts" else 0.0,
            fixed_fee=fixed_fee, point_rub=point_rub,
            leverage=leverage, fixed_units=fixed_units,
            day_stop=cfg.get("day_stop", 0.0),
            lot_size=lot_size, slippage_pct=slippage,
            longs_only=cfg.get("longs_only", False),
            same_day_exit=cfg.get("same_day_exit", False)).run(
            STRAT_FUNCS[name](train, params)
        )

        results["strategies"][name] = {
            "chosen": {**params, "kind": kind, "a": a, "b": b, "trail": trail},
            "train": train_out,
            "test": test_out,
            "skipped": False,
        }

    return results


# ------------------------------------------------------ walk-forward ------
WARMUP = 500  # signal warm-up bars prepended to each OOS fold (must cover the
              # longest indicator window across all strategy grids)
def walk_forward(df: pd.DataFrame, name: str, grid, intraday: bool,
                 train_days: int, oos_days: int, cfg: Dict,
                 ticker: str = "", min_trades: int = 5) -> List[Dict]:
    """Rolling walk-forward: pick best param on train window, apply on next oos window.

    Unless cfg["carry_positions"] is False, a position still open at a fold
    boundary is handed to the next fold instead of being force-closed: the
    boundary is an artefact of how the history is sliced, not a trading rule.
    The next fold still starts from the nominal initial capital, adjusted for the
    cash the carried position already consumed (same accounting the live engine
    uses when it restores a position after a restart).
    """
    import time as _time
    n = len(df)
    folds = []
    starts = list(range(train_days, n - oos_days + 1, oos_days))
    total = len(starts)
    carry = cfg.get("carry_positions", True)
    lot_size = cfg.get("lot_size", 1)
    slippage = cfg.get("slippage_pct", 0.0)
    point_rub = cfg.get("point_rub", 1.0)
    capital = cfg.get("initial_capital", DEFAULT_INITIAL_CAPITAL)
    open_pos = None
    done = 0
    t0 = _time.time()
    last_print = 0.0
    for fold_no, i in enumerate(starts):
        last_fold = fold_no == total - 1
        train = df.iloc[i - train_days:i].reset_index(drop=True)
        oos = df.iloc[i:i + oos_days].reset_index(drop=True)
        best, best_score = None, -np.inf
        exits = cfg.get("exit_grid", [(cfg["sl_atr"], cfg["tp_atr"])])
        specs = [("atr", sl_a, tp_a) for sl_a, tp_a in exits]
        # range_targets are TPfrac:SLfrac pairs, but the spec tuple is (a, b) =
        # (sl, tp) because the Simulator takes sl_frac=a / tp_frac=b.
        specs += [("range", sl_f, tp_f) for tp_f, sl_f in cfg.get("range_targets", [])]
        specs += [("pts", sl_p, tp_p) for sl_p, tp_p in cfg.get("pt_targets", [])]
        sell_fee = cfg["fixed_fee"] if cfg.get("fee_selection", False) else 0.0
        sell_comm = cfg["commission"] if cfg.get("fee_selection", False) else 0.0
        for params in grid:
            sig = STRAT_FUNCS[name](train, params)
            for kind, a, b in specs:
                for trail in cfg["trail_grid"]:
                    m = Simulator(
                        train, eod_flat=intraday, risk_pct=cfg["risk_pct"],
                        sl_atr=a if kind == "atr" else cfg["sl_atr"],
                        tp_atr=b if kind == "atr" else cfg["tp_atr"],
                        commission_pct=sell_comm,
                        trail_atr=trail if kind == "atr" else 0.0,
                        tp_frac=b if kind == "range" else 0.0,
                        sl_frac=a if kind == "range" else 0.0,
                        tp_pts=b if kind == "pts" else 0.0,
                        sl_pts=a if kind == "pts" else 0.0,
                        fixed_fee=sell_fee,
                        point_rub=cfg.get("point_rub", 1.0),
                        leverage=cfg.get("leverage", 1.0),
                        fixed_units=cfg.get("fixed_units", 0),
                        day_stop=cfg.get("day_stop", 0.0),
                        longs_only=cfg.get("longs_only", False),
                        lot_size=lot_size, slippage_pct=slippage,
                        same_day_exit=cfg.get("same_day_exit", False)).run(sig)
                    if m["n_trades"] >= min_trades and m["sharpe"] > best_score:
                        best_score, best = m["sharpe"], (params, kind, a, b, trail)
        if best is None and open_pos is None:
            continue
        if best is None:
            # No selectable candidate, but a position is still open: run the
            # window with a flat signal so its stop/target is honoured on time.
            params, kind, a, b, trail = {}, "atr", cfg["sl_atr"], cfg["tp_atr"], 0.0
            oos_sig = np.zeros(len(oos), dtype=int)
            warm = 0
        else:
            params, kind, a, b, trail = best
            # warm the OOS signal with real pre-fold history so the first bars of
            # a fold can trade (windows > fold start are known facts, not
            # look-ahead); without this the first ~n bars would be NaN signals.
            warm = min(WARMUP, len(train))
            oos_sig = STRAT_FUNCS[name](
                pd.concat([train.tail(warm), oos], ignore_index=True), params)
        # The ATR is warmed the same way the signal is - otherwise the fold-local
        # EWM re-seeds on the fold's first bar and biases stops and sizing.
        atr_warm = None
        if warm:
            atr_warm = compute_atr(
                pd.concat([train.tail(warm), oos], ignore_index=True),
                DEFAULT_ATR_PERIOD).to_numpy()[warm:]
        init_cash = None
        if open_pos is not None:
            init_cash = capital - (open_pos["dir"] * open_pos["units"]
                                   * open_pos["entry"] * point_rub
                                   + open_pos["open_comm"])
        m = Simulator(
            oos, atr_series=atr_warm, lot_size=lot_size, slippage_pct=slippage,
            eod_flat=intraday, risk_pct=cfg["risk_pct"],
            sl_atr=a if kind == "atr" else cfg["sl_atr"],
            tp_atr=b if kind == "atr" else cfg["tp_atr"],
            commission_pct=cfg["commission"],
            trail_atr=trail if kind == "atr" else 0.0,
            tp_frac=b if kind == "range" else 0.0,
            sl_frac=a if kind == "range" else 0.0,
            tp_pts=b if kind == "pts" else 0.0,
            sl_pts=a if kind == "pts" else 0.0,
            fixed_fee=cfg.get("fixed_fee", 0.0),
            point_rub=cfg.get("point_rub", 1.0),
            leverage=cfg.get("leverage", 1.0),
            fixed_units=cfg.get("fixed_units", 0),
            day_stop=cfg.get("day_stop", 0.0),
            longs_only=cfg.get("longs_only", False),
            same_day_exit=cfg.get("same_day_exit", False)).run(
            oos_sig[warm:], initial_position=open_pos, initial_cash=init_cash,
            close_at_end=last_fold or not carry)
        open_pos = m["open_position"] if carry else None
        oos_days_n = len(oos["timestamp"].dt.date.unique())
        folds.append({"oos": m,
                      "params": {**params, "kind": kind, "a": a, "b": b,
                                 "trail": trail},
                      "ticker": ticker, "days": oos_days_n,
                      "year": int(oos["timestamp"].iloc[0].year)})
        done = len(folds)
        now = _time.time()
        if done == total or (done % 5 == 0 and now - last_print >= 5.0) or now - last_print >= 60.0:
            last_print = now
            pnl = m["total_pnl"]
            oos_start = oos["timestamp"].iloc[0].date()
            print(f"  [{ticker or name}] {name}: fold {done}/{total} "
                  f"({oos_start}), OOS pnl {pnl:+.0f}, {m['n_trades']} trades, "
                  f"{now - t0:.0f}s elapsed", flush=True)
    return folds


def walk_forward_all(files, intraday: bool, cfg: Dict,
                     train_days: int, oos_days: int,
                     grids: Dict = None,
                     min_trades: int = 5) -> Dict[str, List[Dict]]:
    if grids is None:
        grids = strategy_grids()
    per = {name: [] for name in grids}
    for f in files:
        df = load_candle_data(f)
        if len(df) < train_days + oos_days + 30:
            continue
        print(f"[walk] {f.stem}: {len(df)} bars, {len(grids)} strategies, "
              f"train={train_days} oos={oos_days}", flush=True)
        for name, grid in grids.items():
            folds = walk_forward(df, name, grid, intraday,
                                 train_days, oos_days, cfg,
                                 ticker=f.stem, min_trades=min_trades)
            per[name].extend(folds)
    return per


def report_walk(per: Dict[str, List[Dict]]):
    print("\n" + "=" * 118)
    print("WALK-FORWARD  (rolling train -> out-of-sample, 1% risk per trade, flat at end of day)")
    print("=" * 118)
    print(f"{'strategy':18} {'folds':>5} {'OOS pnl%':>9} {'avg ret%':>9} {'pos%':>5} "
          f"{'avg win%':>8} {'avg maxDD%':>10} {'avg trades':>10}")
    print("-" * 118)
    for name, folds in per.items():
        if not folds:
            print(f"{name:18} {'-':>5} (no out-of-sample folds)")
            continue
        pnls = [f["oos"]["total_pnl"] / DEFAULT_INITIAL_CAPITAL * 100 for f in folds]
        rets = [f["oos"]["total_return"] for f in folds]
        wins = [f["oos"]["win_rate"] for f in folds]
        dds = [f["oos"]["max_drawdown"] for f in folds]
        trs = [f["oos"]["n_trades"] for f in folds]
        n = len(folds)
        pos = sum(1 for p in pnls if p > 0)
        print(f"{name:18} {n:>5} {sum(pnls):>8.2f}% {np.mean(rets):>8.2f}% "
              f"{pos / n * 100:>4.0f}% {np.mean(wins):>7.1f}% {np.mean(dds):>9.2f}% "
              f"{int(np.mean(trs)):>8}")
    print("=" * 118)


def report_walk_detail(per: Dict[str, List[Dict]], currency: str = "RUB"):
    """Per-ticker breakdown of out-of-sample profit from walk-forward folds."""
    rows = []
    for name, folds in per.items():
        by_ticker = {}
        for f in folds:
            t = f["ticker"]
            if t not in by_ticker:
                by_ticker[t] = {"pnl": 0.0, "days": 0, "n": 0, "win": [], "pos": 0}
            by_ticker[t]["pnl"] += f["oos"]["total_pnl"]
            by_ticker[t]["days"] += f["days"]
            by_ticker[t]["n"] += 1
            by_ticker[t]["win"].append(f["oos"]["win_rate"])
            by_ticker[t]["pos"] += 1 if f["oos"]["total_pnl"] > 0 else 0
        for t, st in by_ticker.items():
            avg_day = st["pnl"] / st["days"] if st["days"] else 0.0
            avg_win = np.mean(st["win"]) if st["win"] else 0.0
            rows.append({
                "strategy": name, "ticker": t,
                "avg_day": avg_day, "total": st["pnl"],
                "days": st["days"], "folds": st["n"], "win": avg_win,
                "pos": st["pos"],
            })

    rows.sort(key=lambda r: r["avg_day"], reverse=True)

    print("\n" + "=" * 118)
    print(f"PER-TICKER OUT-OF-SAMPLE PROFIT  (walk-forward, capital = 100000 {currency}, sorted by avg profit/day)")
    print("=" * 118)
    print(f"{'strategy':18} {'instrument':10} {'avg ' + currency + '/day':>11} {'total ' + currency:>11} "
          f"{'days':>6} {'folds':>5} {'pos%':>5} {'avg win%':>8}")
    print("-" * 118)
    for r in rows:
        print(f"{r['strategy']:18} {r['ticker']:10} {r['avg_day']:>11.2f} {r['total']:>11.2f} "
              f"{r['days']:>6} {r['folds']:>5} {r['pos'] / r['folds'] * 100:>4.0f}% "
              f"{r['win']:>7.1f}%")
    print("=" * 118)


def report_year(per: Dict[str, List[Dict]], currency: str = "RUB"):
    """Per-strategy, per-ticker, per-year out-of-sample profit to check regime dependence."""
    rows = []
    for name, folds in per.items():
        by_key = {}
        for f in folds:
            key = (name, f["ticker"], f["year"])
            if key not in by_key:
                by_key[key] = {"pnl": 0.0, "days": 0, "n": 0, "pos": 0}
            by_key[key]["pnl"] += f["oos"]["total_pnl"]
            by_key[key]["days"] += f["days"]
            by_key[key]["n"] += 1
            by_key[key]["pos"] += 1 if f["oos"]["total_pnl"] > 0 else 0
        for (_, t, y), st in sorted(by_key.items()):
            rows.append({
                "strategy": name, "ticker": t, "year": y,
                "pnl": st["pnl"], "days": st["days"],
                "folds": st["n"], "pos": st["pos"],
            })

    print("\n" + "=" * 118)
    print(f"OOS PROFIT BY TICKER & YEAR  (walk-forward, capital = 100000 {currency})")
    print("=" * 118)
    print(f"{'strategy':18} {'instrument':9} {'year':>6} {'total ' + currency:>12} {'days':>6} "
          f"{'folds':>5} {'pos%':>5} {'avg ' + currency + '/day':>11}")
    print("-" * 118)
    for r in rows:
        avg_day = r["pnl"] / r["days"] if r["days"] else 0.0
        print(f"{r['strategy']:18} {r['ticker']:9} {r['year']:>6} {r['pnl']:>12.0f} {r['days']:>6} "
              f"{r['folds']:>5} {r['pos'] / r['folds'] * 100:>4.0f}% {avg_day:>11.2f}")
    print("=" * 118)


def report(results: List[Dict]):
    print("\n" + "=" * 118)
    print("STRATEGY RESEARCH RESULTS  (train = param fit, test = out-of-sample)")
    print("=" * 118)
    hdr = (f"{'ticker':7} {'strategy':18} {'params':26} "
           f"{'ret%':>7} {'sharpe':>6} {'trn_sh':>6} {'trades':>6} {'win%':>5} {'avgR%':>6} {'maxDD%':>6}")
    print(hdr)
    print("-" * 118)

    agg = {}  # strategy -> list of test metrics
    rows = []
    for r in results:
        if r.get("skipped"):
            print(f"{r['ticker']:7} SKIPPED: {r.get('reason', '')}")
            continue
        for name, s in r["strategies"].items():
            if s.get("skipped"):
                continue
            t = s["test"]
            tr = s["train"]
            params = ",".join(f"{k}={v}" for k, v in s["chosen"].items())
            print(f"{r['ticker']:7} {name:18} {params:26} "
                  f"{t['total_return']:>6.2f}% {t['sharpe']:>6.2f} {tr['sharpe']:>6.2f} "
                  f"{t['n_trades']:>6} {t['win_rate']:>4.1f}% {t['avg_ret_per_trade']:>5.2f}% "
                  f"{t['max_drawdown']:>5.2f}%")
            agg.setdefault(name, []).append(t)
            rows.append((r["ticker"], name, params, t, tr["sharpe"]))

    print("-" * 118)
    print("Average out-of-sample result per strategy:")
    for name, vals in agg.items():
        mean_sh = float(np.mean([v["sharpe"] for v in vals]))
        mean_ret = float(np.mean([v["total_return"] for v in vals]))
        mean_win = float(np.mean([v["win_rate"] for v in vals]))
        mean_dd = float(np.mean([v["max_drawdown"] for v in vals]))
        print(f"  {name:18} mean_sharpe={mean_sh:+.2f}  mean_ret={mean_ret:+.2f}%  "
              f"mean_win={mean_win:.1f}%  mean_maxDD={mean_dd:.2f}%")

    ranked = sorted(rows, key=lambda x: x[3]["total_return"], reverse=True)
    print("-" * 118)
    print("TOP-10 combos by out-of-sample return:")
    for ticker, name, params, t, tr_sh in ranked[:10]:
        print(f"  {ticker:6} {name:18} {params:26} ret={t['total_return']:>7.2f}%  "
              f"sharpe={t['sharpe']:>5.2f}  win={t['win_rate']:>4.1f}%  "
              f"avgR={t['avg_ret_per_trade']:>5.2f}%  dd={t['max_drawdown']:>5.2f}%  "
              f"trades={t['n_trades']:>3}")
    print("=" * 118)


def main():
    import argparse

    parser = argparse.ArgumentParser(description="Strategy research backtester")
    parser.add_argument("--data", default="data/candles",
                        help="folder with CSV candle files")
    parser.add_argument("--granularity", choices=["intraday", "daily"], default="daily",
                        help="intraday = flat at end of day, daily = positions can run")
    parser.add_argument("--mode", choices=["split", "walk"], default="split",
                        help="split = single 70/30 train/test; walk = rolling walk-forward")
    parser.add_argument("--train", type=int, default=None,
                        help="walk-forward train window (bars); default 840 intraday / 126 daily")
    parser.add_argument("--oos", type=int, default=None,
                        help="walk-forward out-of-sample step (bars); default 210 intraday / 21 daily")
    parser.add_argument("--sl", type=float, default=DEFAULT_SL_ATR,
                        help="initial stop loss in ATR units")
    parser.add_argument("--tp", type=float, default=DEFAULT_TP_ATR,
                        help="take profit in ATR units (ignored when a trailing stop is used)")
    parser.add_argument("--commission", type=float, default=DEFAULT_COMMISSION_PCT,
                        help="commission per side in percent of notional")
    parser.add_argument("--fee", type=float, default=0.0,
                        help="fixed commission in RUB per fill (one side), e.g. 30 for futures")
    parser.add_argument("--point-rub", type=float, default=1.0,
                        help="RUB value of one price point (futures multiplier), default 1.0 for stocks")
    parser.add_argument("--leverage", type=float, default=1.0,
                        help="notional leverage cap vs equity (e.g. 10 for ~10%% margin futures)")
    parser.add_argument("--units", type=int, default=0,
                        help="fixed number of contracts per trade (0 = risk-based sizing)")
    parser.add_argument("--risk", type=float, default=1.0,
                        help="risk per trade in percent of equity")
    parser.add_argument("--trails", type=str, default="0,2,3",
                        help="trailing-stop grid in ATR units (0 = fixed SL/TP)")
    parser.add_argument("--exits", type=str, default=None,
                        help="grid of SL:TP pairs in ATR units, e.g. '2:3,3:4.5,5:7.5' "
                             "(default: single pair from --sl/--tp)")
    parser.add_argument("--range-targets", type=str, default=None,
                        help="grid of TPfrac:SLfrac pairs scaled to the day range, "
                             "e.g. '0.3:0.5,0.5:1.0' (alternate exit mode to --exits)")
    parser.add_argument("--pt-targets", type=str, default=None,
                        help="grid of SLpts:TPpts pairs in index points, "
                             "e.g. '3:4,4:6' (fixed-point exit mode)")
    parser.add_argument("--fee-selection", action="store_true",
                        help="deprecated: fee-aware selection is now the default")
    parser.add_argument("--fee-blind-selection", action="store_true",
                        help="select parameters on GROSS (pre-commission) results, "
                             "the old default; selection then favours parameters "
                             "that trade more often than they should")
    parser.add_argument("--lot", type=int, default=1,
                        help="exchange lot size; position size is rounded down to "
                             "a whole number of lots (MOEX trades lots, not shares)")
    parser.add_argument("--slippage", type=float, default=0.0,
                        help="adverse slippage per fill, in percent of price "
                             "(applied to entries and exits alike)")
    parser.add_argument("--no-carry-positions", action="store_true",
                        help="force-close an open position at every walk-forward "
                             "fold boundary (the old behaviour; the boundary is an "
                             "artefact of slicing, so this manufactures exits)")
    parser.add_argument("--longs-only", action="store_true",
                        help="never open short positions (clip -1 signals to 0)")
    parser.add_argument("--same-day", action="store_true",
                        help="no-overnight mode: enter at the open signaled by the "
                             "prior bar, force-flat at this bar's close (futures/"
                             "stock day sessions, no overnight risk)")
    parser.add_argument("--min-trades", type=int, default=5,
                        help="minimum trades a candidate needs to be selectable "
                             "(robustness bar); default 5")
    parser.add_argument("--day-stop", type=float, default=0.0,
                        help="per-day loss lockout in RUB: stop trading for the "
                             "rest of the day after the day's realized loss "
                             "reaches this amount (0 = off)")
    parser.add_argument("--tickers", type=str, default=None,
                        help="comma-separated ticker filter (default: all files in --data)")
    parser.add_argument("--strategy", type=str, default=None,
                        help="run only a single strategy family (e.g. donchian_breakout)")
    parser.add_argument("--per", action="store_true",
                        help="in walk mode, print per-ticker detail table instead of strategy summary")
    parser.add_argument("--byyear", action="store_true",
                        help="in walk mode, print per-year OOS profit table instead of strategy summary")
    parser.add_argument("--currency", default="RUB",
                        help="label used in reports (data prices should match), e.g. RUB or USD")
    args = parser.parse_args()

    files = sorted(Path(args.data).glob("*.csv"))
    if args.tickers:
        allowed = {t.strip().upper() for t in args.tickers.split(",")}
        files = [f for f in files if f.stem.upper() in allowed]
    if not files:
        print(f"No CSV files in {args.data}/")
        print("Download data first: python yahoo_data_downloader.py")
        sys.exit(1)

    intraday_mode = args.granularity == "intraday"
    scale = "min" if "1min" in args.data.lower() else "bar"
    min_train = 31500 if scale == "min" else None  # ~60 days of 1-min bars
    min_oos = 10500 if scale == "min" else None    # ~20 days of 1-min bars

    train_n = args.train if args.train is not None else (
        min_train if min_train else (840 if intraday_mode else 126))
    oos_n = args.oos if args.oos is not None else (
        min_oos if min_oos else (210 if intraday_mode else 21))

    grids = strategy_grids(scale)
    if args.strategy:
        grids = {k: v for k, v in grids.items() if k == args.strategy}
        if not grids:
            print(f"Unknown strategy '{args.strategy}'. Known: "
                  f"{', '.join(strategy_grids(scale).keys())}")
            sys.exit(1)

    cfg = {
        "sl_atr": args.sl,
        "tp_atr": args.tp,
        "commission": args.commission,
        "fixed_fee": args.fee,
        "fee_selection": not args.fee_blind_selection,
        "day_stop": args.day_stop,
        "point_rub": args.point_rub,
        "leverage": args.leverage,
        "fixed_units": args.units,
        "risk_pct": args.risk,
        "longs_only": args.longs_only,
        "same_day_exit": args.same_day,
        "lot_size": args.lot,
        "slippage_pct": args.slippage,
        "carry_positions": not args.no_carry_positions,
        "trail_grid": tuple(float(x) for x in args.trails.split(",")),
    }
    if args.exits is not None:
        if args.exits.strip().lower() in ("", "none"):
            cfg["exit_grid"] = []
        else:
            try:
                cfg["exit_grid"] = [
                    tuple(float(x) for x in pair.split(":"))
                    for pair in args.exits.split(",")
                ]
            except ValueError:
                print("--exits must be comma-separated SL:TP pairs, e.g. 2:3,3:4.5")
                sys.exit(1)
    if args.range_targets:
        try:
            cfg["range_targets"] = [
                (float(t), float(s))
                for t, s in (pair.split(":") for pair in args.range_targets.split(","))
            ]
        except ValueError:
            print("--range-targets must be comma-separated TPfrac:SLfrac pairs, "
                  "e.g. 0.3:0.5,0.5:1.0")
            sys.exit(1)
    if args.pt_targets:
        try:
            cfg["pt_targets"] = [
                tuple(float(x) for x in pair.split(":"))
                for pair in args.pt_targets.split(",")
            ]
        except ValueError:
            print("--pt-targets must be comma-separated SLpts:TPpts pairs, "
                  "e.g. 3:4,4:6")
            sys.exit(1)

    if args.mode == "walk":
        per = walk_forward_all(files, intraday=intraday_mode, cfg=cfg,
                               train_days=train_n, oos_days=oos_n,
                               grids=grids, min_trades=args.min_trades)
        if args.per:
            report_walk_detail(per, currency=args.currency)
        elif args.byyear:
            report_year(per, currency=args.currency)
        else:
            report_walk(per)
    else:
        results = [run_instrument(f, intraday=intraday_mode, cfg=cfg,
                                  grids=grids, min_trades=args.min_trades)
                   for f in files]
        report(results)


if __name__ == "__main__":
    main()