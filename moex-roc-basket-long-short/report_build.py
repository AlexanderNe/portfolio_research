"""Builds the report package for the MOEX daily ROC-momentum basket with
LONGS AND SHORTS enabled BUT NO LEVERAGE (short notional <= cash) on the
40-name expanded universe.

Outputs into ./report:
  images/*.png   Russian-labelled charts
  tables/*.csv   risk scan (lev 1), leverage scan (reference only), by-year,
                 by-name, trade stats, long/short split, delta 40-vs-25,
                 pooled equity

No margin financing and no short-borrow cost are modelled for the leverage-scan
rows (reference only). The no-leverage rows are economy-clean: notional <= cash
on every position.

Walk results are cached in tables/_cache under a key that covers the config,
the parameter grid, the walk-forward windows, the universe AND a content hash of
the source CSVs - refreshing the bars or changing a parameter invalidates the
cache instead of silently returning the previous run. `--no-cache` forces a full
recompute.

Pooled and per-name equity are mark-to-market: realised PnL plus open positions
revalued at each daily close.
"""
import hashlib
import json
import pickle
import sys
from pathlib import Path
import pandas as pd
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

import strategy_research as s
from universe import ORIG, NEW, BASKET40, moex_year_returns

DATA = "data/candles_ru_daily"
OUT = Path("report")
IMG = OUT / "images"
TBL = OUT / "tables"
CACHE = TBL / "_cache"
GRID = s.strategy_grids("bar")["roc_momentum"]
POOL = 100_000.0
RISKS = [1, 2, 3, 5, 8, 10, 15]
LEVS = [1, 2, 3, 5, 7, 10]
HEADLINE_RISK = 10
HEADLINE_LEV = 1
TRAIN_BARS = 504
OOS_BARS = 126
MIN_TRADES = 3
BARS_PER_YEAR = 252.0
USE_CACHE = True


def cfg(risk, lev):
    return {"commission": 0.04, "risk_pct": risk,
            "fixed_fee": 0.0, "fee_selection": True, "point_rub": 1.0,
            "leverage": lev, "fixed_units": 0, "trail_grid": (0.0,),
            "sl_atr": 2.0, "tp_atr": 3.0, "exit_grid": [(2.0, 3.0)],
            "range_targets": [], "pt_targets": [], "longs_only": False,
            "carry_positions": True}


_FINGERPRINTS = {}


def data_fingerprint(names):
    """Content hash of the CSVs a run reads, so refreshed bars invalidate the cache."""
    key = tuple(sorted(names))
    if key not in _FINGERPRINTS:
        h = hashlib.sha1()
        for t in key:
            fp = Path(DATA) / ("%s.csv" % t)
            h.update(t.encode("utf-8"))
            if fp.exists():
                h.update(hashlib.sha1(fp.read_bytes()).digest())
        _FINGERPRINTS[key] = h.hexdigest()
    return _FINGERPRINTS[key]


def cache_key(risk, lev, names):
    """Everything a run depends on: config, grid, windows, universe and data."""
    payload = json.dumps({
        "cfg": cfg(risk, lev), "grid": GRID, "train": TRAIN_BARS,
        "oos": OOS_BARS, "min_trades": MIN_TRADES,
        "names": sorted(names), "data": data_fingerprint(names),
    }, sort_keys=True, default=str)
    return hashlib.sha1(payload.encode("utf-8")).hexdigest()[:16]


def cache_path(risk, lev, nameset, key):
    return CACHE / ("%s_%s_%s_%s.pkl" % (int(risk), int(lev), nameset, key))


def load_cached(risk, lev, nameset, key):
    p = cache_path(risk, lev, nameset, key)
    if p.exists():
        with open(p, "rb") as f:
            return pickle.load(f)
    return None


def save_cached(risk, lev, nameset, key, folds, trades, nbars):
    CACHE.mkdir(parents=True, exist_ok=True)
    with open(cache_path(risk, lev, nameset, key), "wb") as f:
        pickle.dump({"folds": folds, "trades": trades, "nbars": nbars}, f)


def run_walk(risk, lev, names):
    folds = []
    trades = []
    nbars = 0
    for t in sorted(names):
        fp = Path(DATA) / ("%s.csv" % t)
        if not fp.exists():
            continue
        df = s.load_candle_data(str(fp))
        nbars = max(nbars, len(df))
        if len(df) < 700:
            continue
        for f in s.walk_forward(df, "roc_momentum", GRID, False,
                                TRAIN_BARS, OOS_BARS,
                                cfg(risk, lev), ticker=t, min_trades=MIN_TRADES):
            oos = f["oos"]
            folds.append({"ticker": t, "total_pnl": oos["total_pnl"],
                          "n_trades": oos["n_trades"],
                          "max_drawdown": oos["max_drawdown"]})
            for x in oos["trades"]:
                tr = dict(x)
                tr["ticker"] = t
                trades.append(tr)
    return (pd.DataFrame(folds), pd.DataFrame(trades), nbars)


def get(risk, lev, names):
    nameset = "%d" % len(set(names))
    key = cache_key(risk, lev, names)
    got = load_cached(risk, lev, nameset, key) if USE_CACHE else None
    if got is None:
        print("walk risk=%s lev=%s names=%s (%s) ..." % (risk, lev, nameset, key))
        folds, trades, nbars = run_walk(risk, lev, names)
        got = {"folds": folds, "trades": trades, "nbars": nbars}
        save_cached(risk, lev, nameset, key, folds, trades, nbars)
    return got


_CLOSES = {}


def closes_of(ticker):
    """Daily closes of one name, indexed by normalised date (memoised)."""
    if ticker not in _CLOSES:
        fp = Path(DATA) / ("%s.csv" % ticker)
        d = s.load_candle_data(str(fp))
        _CLOSES[ticker] = (d.set_index(pd.to_datetime(d["timestamp"]).dt.normalize())
                            ["close"].astype(float))
    return _CLOSES[ticker]


def pooled_equity(trades, n_names):
    """Daily pooled equity, mark-to-market.

    pool + realised PnL of closed trades + open positions revalued at each daily
    close. Summing realised PnL alone (the old behaviour) skips the whole life of
    every open position - with a 16-day average hold that hides most of the real
    drawdown.
    """
    pool = POOL * n_names
    if not len(trades):
        return pd.Series(dtype=float)
    tickers = sorted(set(trades["ticker"]))
    idx = None
    for t in tickers:
        c = closes_of(t).index
        idx = c if idx is None else idx.union(c)
    entries = pd.to_datetime(trades["entry_time"]).dt.normalize()
    exits = pd.to_datetime(trades["exit_time"]).dt.normalize()
    idx = idx[(idx >= entries.min()) & (idx <= exits.max())]
    realized = _daily_pnl(trades).reindex(idx, fill_value=0.0).cumsum()
    unreal = np.zeros(len(idx))
    for tk, g in trades.groupby("ticker"):
        vals = closes_of(tk).reindex(idx).ffill().to_numpy()
        a = idx.searchsorted(pd.to_datetime(g["entry_time"]).dt.normalize().to_numpy())
        b = idx.searchsorted(pd.to_datetime(g["exit_time"]).dt.normalize().to_numpy())
        for i0, i1, d, u, e in zip(a, b, g["dir"], g["units"], g["entry"]):
            if i1 > i0:
                unreal[i0:i1] += d * u * (vals[i0:i1] - e)
    return pool + realized + pd.Series(unreal, index=idx)


def pool_metrics(folds, trades, n_names, years):
    n_fold = len(folds)
    total = float(folds["total_pnl"].sum()) if n_fold else 0.0
    n_tr = int(folds["n_trades"].sum() if n_fold else 0)
    pos_f = int((folds["total_pnl"] > 0).sum()) if n_fold else 0
    pool = POOL * n_names
    dd_folds = float(folds["max_drawdown"].mean()) if n_fold else 0.0
    eqv = pooled_equity(trades, n_names)
    pool_dd = float((eqv / eqv.cummax() - 1).min() * 100) if len(eqv) else 0.0
    return {"total": total, "n_trades": n_tr, "pos_folds": pos_f,
            "n_fold": n_fold, "pool_dd": pool_dd, "avg_fold_dd": dd_folds,
            "pool": pool}


def trade_stats(tr):
    pnl = tr["pnl"]
    wins = pnl[pnl > 0]
    loss = pnl[pnl <= 0]
    hold = ((pd.to_datetime(tr["exit_time"]) - pd.to_datetime(tr["entry_time"]))
            .dt.days)
    gw = float(wins.sum()) if len(wins) else 0.0
    gl = float(-loss.sum()) if len(loss) else 0.0
    return {
        "n": int(len(tr)),
        "win": float(len(wins) / len(tr) * 100) if len(tr) else 0.0,
        "avg": float(pnl.mean()) if len(pnl) else 0.0,
        "avg_w": float(wins.mean()) if len(wins) else 0.0,
        "avg_l": float(loss.mean()) if len(loss) else 0.0,
        "pf": float(gw / gl) if gl > 0 else float("inf"),
        "hold_days": float(hold.mean()) if len(hold) else 0.0,
        "max_w": float(pnl.max()) if len(pnl) else 0.0,
        "max_l": float(pnl.min()) if len(pnl) else 0.0,
        "total": float(pnl.sum()) if len(pnl) else 0.0,
        "expectancy": float(pnl.mean()) if len(pnl) else 0.0,
    }


def pct_year(trades, n_names):
    """Per-year pooled PnL. `partial` marks years the OOS window does not cover
    end to end - their % is a fraction of the pool over part of a year, never a
    per-annum rate."""
    pool = POOL * n_names
    rows = []
    ts = pd.to_datetime(trades["exit_time"])
    for y, g in trades.groupby(ts.dt.year):
        r = trade_stats(g)
        gt = pd.to_datetime(g["exit_time"])
        covered = (gt.max() - gt.min()).days
        rows.append({"year": y, "pnl_RUB": r["total"],
                     "pnl_pct": r["total"] / pool * 100, "trades": r["n"],
                     "win_pct": r["win"], "avg_trade": r["avg"],
                     "profit_factor": r["pf"],
                     "partial": bool(covered < 300)})
    return pd.DataFrame(rows).sort_values("year").reset_index(drop=True)


def ls_by_year(trades, n_names):
    """Long/short split per year - the report used to assert this without computing it."""
    pool = POOL * n_names
    rows = []
    for y, g in trades.groupby(pd.to_datetime(trades["exit_time"]).dt.year):
        sh = float(g.loc[g["dir"] == -1, "pnl"].sum())
        lg = float(g.loc[g["dir"] == 1, "pnl"].sum())
        rows.append({"year": int(y), "short_pnl": sh, "long_pnl": lg,
                     "short_pct": sh / pool * 100, "long_pct": lg / pool * 100,
                     "short_n": int((g["dir"] == -1).sum()),
                     "long_n": int((g["dir"] == 1).sum())})
    return pd.DataFrame(rows).sort_values("year").reset_index(drop=True)


def by_name(trades):
    out = []
    for t, g in trades.groupby("ticker"):
        cur = pooled_equity(g, 1)
        dd = float((cur / cur.cummax() - 1).min() * 100) if len(cur) else 0.0
        lp = g.loc[g["dir"] == 1, "pnl"].sum()
        sp = g.loc[g["dir"] == -1, "pnl"].sum()
        out.append({"ticker": t, "pnl_RUB": g["pnl"].sum(),
                    "pnl_pct_100k": g["pnl"].sum() / POOL * 100,
                    "trades": len(g), "maxDD_pct": dd,
                    "long_pnl": lp, "short_pnl": sp,
                    "long_n": int((g["dir"] == 1).sum()),
                    "short_n": int((g["dir"] == -1).sum())})
    return pd.DataFrame(out).sort_values("pnl_RUB", ascending=False)


def style():
    plt.rcParams["font.family"] = "DejaVu Sans"
    plt.rcParams["axes.grid"] = True
    plt.rcParams["grid.alpha"] = 0.3


def f(x):
    return ("{:,}".format(round(x)).replace(",", " ") if abs(x) >= 1000
            else "{:.1f}".format(x) if abs(x) < 100 else "{:.0f}".format(x))


def _daily_pnl(trades):
    s = pd.to_datetime(trades["exit_time"]).dt.normalize()
    return trades.groupby(s)["pnl"].sum().sort_index()


def chart_pooled(trades, n_names, risk):
    eqv = pooled_equity(trades, n_names)
    eqv = eqv.resample("D").last().ffill()
    dd = (eqv / eqv.cummax() - 1) * 100
    fig, axes = plt.subplots(2, 1, figsize=(11, 7),
                             gridspec_kw={"height_ratios": [2.4, 1]})
    axes[0].plot(eqv.index, eqv, lw=1.5, color="#1257a0")
    axes[0].set_title(
        "Совокупная кривая средств OOS (mark-to-market) — %d бумаг, риск %.0f%%/сделку, "
        "плечо %g×, лонги и шорты\nмакс. просадка %.1f%%, итог %s ₽ (%+.1f%% на пул %s ₽)"
        % (n_names, risk, HEADLINE_LEV, dd.min(), f(eqv.iloc[-1] - POOL * n_names),
           (eqv.iloc[-1] / (POOL * n_names) - 1) * 100,
           f(POOL * n_names)), fontsize=10)
    axes[0].set_ylabel("пул, руб. (%d × 100 тыс.)" % n_names)
    axes[1].fill_between(dd.index, dd, 0, color="#c0392b", alpha=0.4)
    axes[1].set_title("Просадка, %")
    axes[1].set_ylabel("%")
    plt.tight_layout()
    plt.savefig(IMG / "equity_pooled.png", dpi=120)


def chart_pername(trades):
    rows = []
    for t, g in trades.groupby("ticker"):
        rows.append((t, pooled_equity(g, 1)))
    if not rows:
        return
    rows.sort(key=lambda r: r[1].iloc[-1] / r[1].iloc[0])
    cols, n = 8, len(rows)
    gr = (n + cols - 1) // cols
    fig, axes = plt.subplots(gr, cols, figsize=(19, 2.6 * gr))
    axes = axes.ravel()
    for i, (t, cur) in enumerate(rows):
        ax = axes[i]
        ax.plot(cur.index, cur.values, lw=1.1, color="#1257a0")
        pct = cur.iloc[-1] / POOL - 1
        dd = (cur / cur.cummax() - 1).min() * 100
        ax.axhline(POOL, color="0.7", lw=0.7, ls="--")
        ax.set_title("%s  %+5.0f%%  dd %-4.1f%%" % (t, pct * 100, dd),
                     fontsize=8.5, fontweight="bold")
        ax.grid(alpha=0.25, ls=":")
        ax.tick_params(labelsize=6)
    for i in range(n, len(axes)):
        axes[i].set_visible(False)
    fig.suptitle("Индивидуальные кривые бумаг — OOS, 100 тыс. старт, плечо %g×, "
                 "лонги и шорты (отсортировано по доходности)" % HEADLINE_LEV, fontsize=12)
    plt.tight_layout(rect=(0, 0, 1, 0.985))
    plt.savefig(IMG / "equity_pername.png", dpi=110)


SIGNAL_TICKERS = ["SBER", "GAZP", "ALRS", "LKOH", "ROSN", "NLMK",
                  "CHMF", "TATN"]
SIGNAL_BARS = None  # full window (from earliest available bar) — see chart_signals


def chart_signals(ticker, trades):
    """Full daily OHLC candles + real walk-OOS trade markers.

    Candles: green body = up day, red body = down day. Trade markers are NOT
    green/red (they would clash with the candles): blue ^ = BUY (open long /
    close short), orange v = SELL (open short / close long). Bottom panel =
    mark-to-market sub-account equity (open positions revalued at each daily
    close, not just closed trades). Extremely wide, stack a scrollbar / save
    as one image.
    """
    fp = Path(DATA) / ("%s.csv" % ticker)
    if not fp.exists():
        return
    df = s.load_candle_data(str(fp))
    win = df.copy().reset_index(drop=True)
    dts = pd.to_datetime(win["timestamp"]).dt.normalize()
    days = dts.tolist()
    idx = {d: i for i, d in enumerate(days)}

    sub = trades[trades["ticker"] == ticker].copy()
    if len(sub):
        sub["ed"] = pd.to_datetime(sub["entry_time"]).dt.normalize()
        sub["xd"] = pd.to_datetime(sub["exit_time"]).dt.normalize()
        sub = sub[sub["ed"].isin(days) | sub["xd"].isin(days)]

    nwin = len(win)
    fig, (ax, ax2) = plt.subplots(2, 1, sharex=True,
                                  figsize=(8.0 + nwin / 22.0, 9.2),
                                  gridspec_kw={"height_ratios": [3, 1]})

    xs = np.arange(nwin)
    o = win["open"].values
    h = win["high"].values
    l = win["low"].values
    c = win["close"].values
    up = c >= o
    up_c, dn_c = "#26a69a", "#e74c3c"
    cols = np.where(up, up_c, dn_c).tolist()
    ax.vlines(xs, l, h, colors=cols, lw=0.6, zorder=3)
    base = np.where(up, o, c)
    body = np.abs(c - o)
    body[body == 0] = np.finfo(float).eps
    ax.bar(xs, body, bottom=base, width=0.66, color=cols,
           edgecolor=cols, linewidth=0.3, zorder=4, align="center")

    pnl_win = 0.0
    for _, t in sub.iterrows():
        pnl_win += t["pnl"]
        x0 = idx.get(t["ed"]) if t["ed"] in days else None
        x1 = idx.get(t["xd"]) if t["xd"] in days else None
        if x0 is not None and x1 is not None:
            ax.plot([x0, x1], [t["entry"], t["exit"]], color="0.4", lw=0.9,
                    zorder=5)
        for act in ("open", "close"):
            d = t["ed"] if act == "open" else t["xd"]
            if d not in days:
                continue
            x = idx[d]
            price = t["entry"] if act == "open" else t["exit"]
            buy = (t["dir"] == 1) == (act == "open")
            ax.scatter(x, price, marker="^" if buy else "v", s=90,
                       color="#1257a0" if buy else "#e67e22",
                       edgecolor="white", lw=0.9, zorder=6)
            if act == "close":
                pc = t["pnl"]
                ax.annotate("%s%s ₽" % ("+" if pc >= 0 else "−",
                                        f(abs(pc))),
                            xy=(x, price), xytext=(7, 0),
                            textcoords="offset points",
                            va="center", ha="left", fontsize=8,
                            color="#1257a0" if pc >= 0 else "#e67e22",
                            bbox=dict(boxstyle="round,pad=0.12",
                                      fc="white", ec="0.7", alpha=0.85),
                            zorder=7)
    step = max(1, (len(win) // 10) // 5 * 5)
    ax.tick_params(labelbottom=False)
    ax.set_ylim(l.min(), h.max())
    ax.set_title(
        "%s  %s .. %s   (риск %g%%, плечо %g×, лонги+шорты)\nсделок в окне: %d, "
        "PnL по ним: %s ₽"
        % (ticker, days[0].date(), days[-1].date(),
           HEADLINE_RISK, HEADLINE_LEV, len(sub), f(pnl_win)),
        fontsize=13, fontweight="bold")
    ax.set_ylabel("цена, ₽", fontsize=11)
    ax.grid(alpha=0.25, ls=":")
    ax.set_xlim(-1, len(win))
    handles = [
        plt.Line2D([0], [0], marker="^", color="w", ms=12,
                   markerfacecolor="#1257a0", mec="white", lw=0,
                   label="BUY — открытие лонга / закрытие шорта"),
        plt.Line2D([0], [0], marker="v", color="w", ms=12,
                   markerfacecolor="#e67e22", mec="white", lw=0,
                   label="SELL — открытие шорта / закрытие лонга"),
        plt.Line2D([0], [0], marker="s", color="w", ms=9,
                   markerfacecolor=up_c, mew=0, lw=0,
                   label="свеча: рост дня"),
        plt.Line2D([0], [0], marker="s", color="w", ms=9,
                   markerfacecolor=dn_c, mew=0, lw=0,
                   label="свеча: падение дня")]
    ax.legend(handles=handles, loc="upper left", fontsize=10.5,
              framealpha=0.9, edgecolor="0.6")

    realized = np.zeros(nwin)
    unreal = np.zeros(nwin)
    closes = win["close"].values
    for _, t in sub.iterrows():
        xi = idx.get(t["ed"], 0) if t["ed"] in days else 0
        xj = idx.get(t["xd"], nwin) if t["xd"] in days else nwin
        if xj > xi:
            unreal[xi:xj] += t["dir"] * t["units"] * (closes[xi:xj] - t["entry"])
        if t["xd"] in days:
            realized[xj] += t["pnl"]
    eq = POOL + np.cumsum(realized) + unreal
    ax2.plot(range(nwin), eq, color="#0a0", lw=1.6, zorder=3)
    ax2.axhline(POOL, color="0.6", lw=0.8, ls="--", zorder=2)
    ax2.set_xticks(range(0, nwin, step))
    ax2.set_xticklabels([str(days[i].date()) for i in range(0, nwin, step)],
                        rotation=15, fontsize=10)
    ax2.tick_params(axis="y", labelsize=10)
    ax2.set_title("кривая средств суб-счёта (старт 100 тыс. ₽, mark-to-market: "
                  "открытые позиции переоцениваются по дневному закрытию)",
                  fontsize=10)
    ax2.grid(alpha=0.25, ls=":")
    ax2.set_xlim(-1, nwin)
    plt.tight_layout()
    plt.savefig(IMG / ("signals_%s.png" % ticker.lower()), dpi=120)
    plt.close(fig)


def chart_contribution(trades):
    bn = by_name(trades)
    items = sorted(zip(bn["ticker"], bn["pnl_RUB"]), key=lambda kv: kv[1])
    names = [k for k, _ in items]
    vals = [v for _, v in items]
    colors = ["#27ae60" if v > 0 else "#c0392b" for v in vals]
    fig, ax = plt.subplots(figsize=(9, 12))
    ax.barh(names, vals, color=colors)
    ax.axvline(0, color="k", lw=0.8)
    ax.set_title("Вклад каждой бумаги (OOS, плечо %g×, лонги и шорты), руб."
                 % HEADLINE_LEV, fontsize=11)
    ax.set_xlabel("суммарный PnL по всем OOS-фолдам, руб.")
    for i, v in enumerate(vals):
        ax.text(v if v > 0 else v - 3000, i,
                "%+.0f" % (v / 1000) if abs(v) >= 1000 else "%+.0f" % v,
                va="center", fontsize=6.5)
    plt.tight_layout()
    plt.savefig(IMG / "pername_contribution.png", dpi=120)


def chart_year_bars(ye):
    y = [("%s*" % r["year"]) if r.get("partial") else str(r["year"])
         for r in ye.to_dict("records")]
    b = ye["pnl_pct"].tolist()
    i = ye["IMOEX_pct"].tolist()
    x = np.arange(len(y))
    w = 0.38
    fig, ax = plt.subplots(figsize=(11, 6))
    ax.bar(x - w / 2, b, w, label="Стратегия (пул 4,0 млн руб., плечо %g×, лонги+шорты)"
           % HEADLINE_LEV,
           color=["#27ae60" if v > 0 else "#c0392b" for v in b])
    ax.bar(x + w / 2, i, w, label="Индекс IMOEX (цена, без дивидендов)",
           color="#7f8c8d")
    for xi, (bb, ii) in enumerate(zip(b, i)):
        ax.text(xi - w / 2, bb + (0.4 if bb > 0 else -2.2), "%+.1f" % bb,
                ha="center", fontsize=8)
        ax.text(xi + w / 2, ii + (0.4 if ii > 0 else -2.2), "%+.1f" % ii,
                ha="center", fontsize=8, color="#444")
    ax.axhline(0, color="k", lw=0.8)
    ax.set_xticks(x)
    ax.set_xticklabels(y)
    ax.set_ylabel("% к пулу за год")
    ax.set_title("Результат по годам: стратегия (лонги+шорты, плечо %g×) "
                 "vs индекс IMOEX (цена)\n* — год покрыт OOS-окном не полностью"
                 % HEADLINE_LEV, fontsize=11)
    ax.legend(fontsize=9)
    plt.tight_layout()
    plt.savefig(IMG / "returns_by_year.png", dpi=120)


def chart_year_money(ye):
    y = [("%s*" % r["year"]) if r.get("partial") else str(r["year"])
         for r in ye.to_dict("records")]
    p = ye["pnl_RUB"].tolist()
    x = np.arange(len(y))
    fig, ax = plt.subplots(figsize=(11, 6))
    ax.bar(x, p, color=["#27ae60" if v > 0 else "#c0392b" for v in p],
           width=0.6)
    for xi, pp in enumerate(p):
        ax.text(xi, pp + (0.6e5 if pp > 0 else -1.6e5), "%s" % f(pp),
                ha="center", fontsize=8)
    ax.axhline(0, color="k", lw=0.8)
    ax.set_xticks(x)
    ax.set_xticklabels(y)
    ax.yaxis.set_major_formatter(lambda v, _: "%dk" % (v / 1000))
    ax.set_ylabel("тыс. руб.")
    ax.set_title("Прибыль по годам (OOS, пул 4,0 млн руб., плечо %g×, лонги и шорты)"
                 % HEADLINE_LEV, fontsize=11)
    plt.tight_layout()
    plt.savefig(IMG / "pnl_by_year.png", dpi=120)


def chart_hist(trades):
    longs = trades.loc[trades["dir"] == 1, "pnl"]
    shorts = trades.loc[trades["dir"] == -1, "pnl"]
    def clip(pnl):
        lo, hi = pnl.quantile(0.02), pnl.quantile(0.98)
        return pnl[(pnl >= lo) & (pnl <= hi)]
    l, sh = clip(longs), clip(shorts)
    fig, ax = plt.subplots(figsize=(11, 5.5))
    ax.hist(sh / 1000, bins=50, alpha=0.6, color="#c0392b",
            label="шорты (среднее %+.0f тыс.)" % (shorts.mean() / 1000))
    ax.hist(l / 1000, bins=50, alpha=0.6, color="#27ae60",
            label="лонги (среднее %+.0f тыс.)" % (longs.mean() / 1000))
    ax.axvline(0, color="k", lw=0.9)
    ax.set_xlabel("PnL сделки, тыс. руб. (коридор 2–98 перцентиль)")
    ax.set_ylabel("количество сделок")
    ax.set_title("Распределение PnL: лонги vs шорты (%d сделок, OOS)"
                 % len(trades), fontsize=11)
    ax.legend(fontsize=9)
    plt.tight_layout()
    plt.savefig(IMG / "pnl_histogram.png", dpi=120)


def chart_risk_return(risk_df):
    fig, ax = plt.subplots(figsize=(9.5, 6))
    ax.plot(risk_df["risk%"], risk_df["per_year_%"], "o-", color="#1257a0",
            lw=2, ms=6, label="Лонги+шорты, плечо %g×" % HEADLINE_LEV)
    for xi, yi in zip(risk_df["risk%"], risk_df["per_year_%"]):
        ax.annotate("%+.1f%%" % yi, (xi, yi), textcoords="offset points",
                    xytext=(0, 8), ha="center", fontsize=8)
    ax2 = ax.twinx()
    ax2.plot(risk_df["risk%"], risk_df["pool_maxDD_%"], "s--",
             color="#c0392b", lw=2, ms=6, label="DD, лонги+шорты")
    ax.set_xlabel("Риск на сделку, % от 100-тысячного портфеля бумаги")
    ax.set_ylabel("%/год (пул 4,0 млн руб.)", color="#1257a0")
    ax2.set_ylabel("макс. просадка, %", color="#c0392b")
    ax.set_title("Риск → доходность: лонги+шорты (плечо %g×)" % HEADLINE_LEV, fontsize=11)
    ax.legend(loc="upper left", fontsize=9)
    ax2.legend(loc="lower right", fontsize=9)
    plt.tight_layout()
    plt.savefig(IMG / "risk_return.png", dpi=120)


def chart_leverage(lev_df):
    fig, ax = plt.subplots(figsize=(9, 6))
    ax.plot(lev_df["leverage"], lev_df["per_year_%"], "o-", color="#1257a0",
            lw=2, ms=6, label="Доходность в год, %/год")
    for xi, yi in zip(lev_df["leverage"], lev_df["per_year_%"]):
        ax.annotate("%+.1f%%" % yi, (xi, yi), textcoords="offset points",
                    xytext=(0, 8), ha="center", fontsize=8)
    ax2 = ax.twinx()
    ax2.plot(lev_df["leverage"], lev_df["pool_maxDD_%"], "s--",
             color="#c0392b", lw=2, ms=6, label="Макс. просадка пула, %")
    for xi, yi in zip(lev_df["leverage"], lev_df["pool_maxDD_%"]):
        ax2.annotate("%.1f%%" % yi, (xi, yi), textcoords="offset points",
                     xytext=(0, -16), ha="center", fontsize=8, color="#c0392b")
    ax.set_xlabel("Плечо (кап суммарного номинала к деньгам, риск %g%%/сделку)"
                  % HEADLINE_RISK)
    ax.set_ylabel("%/год (пул 4,0 млн руб.)", color="#1257a0")
    ax2.set_ylabel("макс. просадка, %", color="#c0392b")
    ax.set_title("Плечо → доходность: лонги и шорты, риск %g%%, 40 бумаг (OOS)"
                 % HEADLINE_RISK, fontsize=11)
    ax.legend(loc="upper left", fontsize=9)
    ax2.legend(loc="lower right", fontsize=9)
    plt.tight_layout()
    plt.savefig(IMG / "leverage_return.png", dpi=120)


# ---------------------------------------------------------------------------
# report/report.md + report/report.html (Russian narration + live numbers)
# ---------------------------------------------------------------------------

import re as _re

HTML_CSS = """
  body { font-family: -apple-system, "Segoe UI", Roboto, Arial, sans-serif;
         max-width: 1100px; margin: 0 auto; padding: 24px 32px 80px;
         color: #1a1a1a; line-height: 1.55; background: #fff; }
  h1 { font-size: 1.7em; border-bottom: 2px solid #1257a0; padding-bottom: 8px; }
  h2 { font-size: 1.3em; color: #1257a0; margin-top: 40px; }
  table { border-collapse: collapse; margin: 16px 0; width: 100%; font-size: 0.92em; }
  th, td { border: 1px solid #ccc; padding: 6px 10px; text-align: left; }
  th { background: #eef3fb; }
  tr:nth-child(even) td { background: #fafafa; }
  code { background: #f2f2f2; padding: 1px 4px; border-radius: 3px; font-size: 0.9em; }
  ul, ol { padding-left: 24px; }
  em { color: #555; }
  img.normal { max-width: 100%; height: auto; display: block; margin: 16px auto; }
  .scroll { overflow-x: auto; border: 1px solid #ddd; border-radius: 6px;
            margin: 8px 0 20px; background: #fbfbfb; }
  .scroll img { display: block; height: auto; }
  .signal-name { margin: 20px 0 4px; font-weight: bold; color: #1257a0; }
  .hint { font-weight: normal; font-size: 0.85em; color: #777; }
  .metrics { color: #333; font-size: 0.9em; margin: 4px 0 8px; }
  .caption { color: #555; font-size: 0.9em; margin: 4px 0 8px; }
""".strip("\n")

# static Russian narrative blocks (unchanged between runs)
MD_RISKS = (
    "1. **Это потолок, а не рекомендация.** Здесь **не моделируются** реальные издержки:\n"
    "   - **плата за заёмный шорт** (MOEX/брокер берёт за шорты; размер зависит от\n"
    "     бумаги и срока) — не учтена;\n"
    "   - гэпы на шортах (лимитные дни, когда стоп 2·ATR «проколот» открытием);\n"
    "   - влияние собственных сделок на цену малоликвидных бумаг (номинал до 100\n"
    "     тыс. ₽ на бумагу при риске до 15%%);\n"
    "   - **дивидендные отсечки.** Дневные бары MOEX — «грязные» цены без поправки\n"
    "     на дивиденды. Лонг, удерживаемый через отсечку, видит гэп вниз и фиксирует\n"
    "     убыток, хотя в реальности получил бы дивиденд; шорт получает этот гэп как\n"
    "     прибыль, хотя при шорте акции дивиденд списывается в пользу кредитора, а\n"
    "     при шорте фьючерсом гэпа нет вовсе (дивиденд уже в базисе). Эффект НЕ\n"
    "     смоделирован и смещает разбивку лонги/шорты в пользу шортов;\n"
    "   - **маржинальное финансирование.** При плече %g× часть номинала лонгов\n"
    "     финансируется заёмными деньгами; процент по маржинальному займу не учтён.\n"
    "   Реальная доходность будет ниже; насколько — покажет только живой счёт или\n"
    "   реплей на реальном потоке минуток.\n"
    "2. **Плечо используется: %g×.** Суммарный номинал позиций допускается до %g×\n"
    "   наличных суб-счёта; издержки заёмного финансирования (см. п.1) не учтены\n"
    "   и снижают фактический плечевой результат.\n"
    "3. **Просадки по отдельным бумагам** в 100-тысячных субсчётах достигают\n"
    "   −50…−130%% и более — это артефакт песочных субсчётов; судить нужно по пулу\n"
    "   целиком (макс. просадка пула приведена в сводке). Распоряжаться деньгами\n"
    "   нужно по пулу целиком.\n"
    "4. IMOEX включён как индекс-прокси ликвидности корзины.\n"
    "5. **Шорты исполняются фьючерсами на бумагу — платы за ночной перенос нет.**\n"
    "   Планируемое исполнение короткой ноги — фьючерсный контракт на ту же бумагу\n"
    "   (а не шорт акции через брокера): у фьючерсов не взимается плата за заём\n"
    "   ценной бумаги и перенос позиции на ночь — «стоимость переноса» заложена в\n"
    "   цену фьючерса (контанго/бэквордация), а отдельной комиссии за овернайт нет.\n"
    "   Итог: главная несмоделированная издержка из п.1 (плата за заёмный шорт)\n"
    "   устраняется на уровне исполнения. Комиссии при торговле фьючерсами в\n"
    "   Т-Инвестициях берутся только за сделки купли/продажи (от рублёвой стоимости\n"
    "   контракта), без платы за удержание позиции на ночь [справка\n"
    "   Т-Банка](https://www.tbank.ru/invest/help/brokerage/account/forts/trade-futures/?card=q2):\n"
    "   на тарифе «Трейдер» это **0,04%%** стоимости фьючерса при дневном обороте до\n"
    "   5 млн ₽ — ровно та комиссия, что уже заложена в бэктест. Единственная\n"
    "   возможная плата при удержании — «перенос непокрытой позиции» — и то только\n"
    "   если на счёте не хватает денег на гарантийное обеспечение. Плечевые позиции\n"
    "   (номинал до %g× наличных) частично покрыты заёмными деньгами, поэтому плата\n"
    "   за перенос непокрытой позиции возможна и должна учитываться при внедрении.\n"
    "   *При этом в бэктесте НЕ учтено:*\n"
    "   - **квартальный ролл.** Фьючерс живёт один квартал; раз в квартал позиция\n"
    "     переносится в следующий ближайший контракт. Стоимость/проскальзывание\n"
    "     перекатки (спред между кварталами) не смоделированы.\n"
    "   - **жидкие фьючерсы есть не на все 40 имён** (примерно на 15: SBER→SBRF,\n"
    "     GAZP→GAZR, CHMF→CHMFM, PLZL→PLZLM, SNGSP→SNGR, TRNFP→TRNF и т.п.). Для\n"
    "     бумаг без фьючерса шорт-нога делается либо через брокера (с платой за\n"
    "     заём), либо пропускается.\n"
    "   - **частичное обеспечение.** Фьючерс требует не весь номинал, а лишь\n"
    "     гарантийное обеспечение (обычно ~10–20%% номинала) — технически это\n"
    "     частичное залоговое «плечо». В бэктесте шорт считался на полный номинал,\n"
    "     поэтому фактическое связывание капитала шорт-ногой будет даже меньше."
) % (HEADLINE_LEV, HEADLINE_LEV, HEADLINE_LEV, HEADLINE_LEV)

HTML_RISKS = (
    "<ol>\n"
    "<li><strong>Это потолок, а не рекомендация.</strong> Здесь <strong>не моделируются</strong> реальные издержки:\n"
    "  <ul>\n"
    "    <li><strong>плата за заёмный шорт</strong> (MOEX/брокер берёт за шорты; размер зависит от бумаги и срока) — не учтена;</li>\n"
    "    <li>гэпы на шортах (лимитные дни, когда стоп 2·ATR «проколот» открытием);</li>\n"
    "    <li>влияние собственных сделок на цену малоликвидных бумаг (номинал до 100 тыс. ₽ на бумагу при риске до 15%%);</li>\n"
    "    <li><strong>дивидендные отсечки.</strong> Дневные бары MOEX — «грязные» цены без поправки на дивиденды. Лонг, удерживаемый через отсечку, видит гэп вниз и фиксирует убыток, хотя в реальности получил бы дивиденд; шорт получает этот гэп как прибыль, хотя при шорте акции дивиденд списывается в пользу кредитора, а при шорте фьючерсом гэпа нет вовсе (дивиденд уже в базисе). Эффект НЕ смоделирован и смещает разбивку лонги/шорты в пользу шортов;</li>\n"
    "    <li><strong>маржинальное финансирование.</strong> При плече %g× часть номинала лонгов финансируется заёмными деньгами; процент по маржинальному займу не учтён.</li>\n"
    "  </ul>\n"
    "  Реальная доходность будет ниже; насколько — покажет только живой счёт или реплей на реальном потоке минуток.</li>\n"
    "<li><strong>Плечо используется: %g×.</strong> Суммарный номинал позиций допускается до %g× наличных суб-счёта; издержки заёмного финансирования (см. п.1) не учтены и снижают фактический плечевой результат.</li>\n"
    "<li><strong>Просадки по отдельным бумагам</strong> в 100-тысячных субсчётах достигают −50…−130%% и более — это артефакт песочных субсчётов; судить нужно по пулу целиком (макс. просадка пула приведена в сводке). Распоряжаться деньгами нужно по пулу целиком.</li>\n"
    "<li>IMOEX включён как индекс-прокси ликвидности корзины.</li>\n"
    "<li><strong>Шорты исполняются фьючерсами на бумагу — платы за ночной перенос нет.</strong> Планируемое исполнение короткой ноги — фьючерсный контракт на ту же бумагу (а не шорт акции через брокера): у фьючерсов не взимается плата за заём ценной бумаги и перенос позиции на ночь — «стоимость переноса» заложена в цену фьючерса (контанго/бэквордация), а отдельной комиссии за овернайт нет. Итог: главная несмоделированная издержка из п.1 (плата за заёмный шорт) устраняется на уровне исполнения. Комиссии при торговле фьючерсами в Т-Инвестициях берутся только за сделки купли/продажи (от рублёвой стоимости контракта), без платы за удержание позиции на ночь (<a href=\"https://www.tbank.ru/invest/help/brokerage/account/forts/trade-futures/?card=q2\">справка Т-Банка</a>): на тарифе «Трейдер» это <strong>0,04%%</strong> стоимости фьючерса при дневном обороте до 5 млн ₽ — ровно та комиссия, что уже заложена в бэктест. Единственная возможная плата при удержании — «перенос непокрытой позиции» — и то только если на счёте не хватает денег на гарантийное обеспечение. Плечевые позиции (номинал до %g× наличных) частично покрыты заёмными деньгами, поэтому плата за перенос непокрытой позиции возможна и должна учитываться при внедрении.\n"
    "  <p><em>При этом в бэктесте НЕ учтено:</em></p>\n"
    "  <ul>\n"
    "    <li><strong>квартальный ролл.</strong> Фьючерс живёт один квартал; раз в квартал позиция переносится в следующий ближайший контракт. Стоимость/проскальзывание перекатки (спред между кварталами) не смоделированы.</li>\n"
    "    <li><strong>жидкие фьючерсы есть не на все 40 имён</strong> (примерно на 15: SBER→SBRF, GAZP→GAZR, CHMF→CHMFM, PLZL→PLZLM, SNGSP→SNGR, TRNFP→TRNF и т.п.). Для бумаг без фьючерса шорт-нога делается либо через брокера (с платой за заём), либо пропускается.</li>\n"
    "    <li><strong>частичное обеспечение.</strong> Фьючерс требует не весь номинал, а лишь гарантийное обеспечение (обычно ~10–20%% номинала) — технически это частичное залоговое «плечо». В бэктесте шорт считался на полный номинал, поэтому фактическое связывание капитала шорт-ногой будет даже меньше.</li>\n"
    "  </ul></li>\n"
    "</ol>"
) % (HEADLINE_LEV, HEADLINE_LEV, HEADLINE_LEV, HEADLINE_LEV)


def _fnum(x, dp=0):
    """8 043 568 / 22,1 (Russian: space thousands, comma decimals)."""
    return format(x, ",.%df" % dp).replace(",", "\x00").replace(".", ",").replace("\x00", " ")


def _snum(x, dp=0):
    sign = "+" if x > 0 else ("−" if x < 0 else "")
    return sign + _fnum(abs(x), dp)


def _rub(x, dp=0):
    return _snum(x, dp) + " ₽"


def _pct(x, dp=1):
    sign = "+" if x > 0 else ("−" if x < 0 else "")
    return sign + _fnum(abs(x), dp) + "%"


def _esc(s):
    return (s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"))


_INLINE_RE = _re.compile(r"(\*\*.+?\*\*|`[^`]+`|\[[^\]]+\]\([^)]+\))")


def _html_inline(s):
    out = []
    for chunk in _INLINE_RE.split(s):
        if not chunk:
            continue
        if chunk.startswith("**") and chunk.endswith("**") and len(chunk) > 4:
            out.append("<strong>%s</strong>" % _esc(chunk[2:-2]))
        elif chunk.startswith("`") and chunk.endswith("`") and len(chunk) > 2:
            out.append("<code>%s</code>" % _esc(chunk[1:-1]))
        elif chunk.startswith("[") and "](" in chunk:
            t, u = chunk[1:-1].split("](", 1)
            out.append('<a href="%s">%s</a>' % (_esc(u[:-1]), _esc(t)))
        else:
            out.append(_esc(chunk))
    return "".join(out)


def _md_table(header, rows):
    lines = ["| " + " | ".join(header) + " |",
             "|" + "|".join(["---"] * len(header)) + "|"]
    lines += ["| " + " | ".join(r) + " |" for r in rows]
    return "\n".join(lines)


def _html_table(header, rows):
    h = "<tr>" + "".join("<th>%s</th>" % _html_inline(c) for c in header) + "</tr>"
    body = "\n".join(
        "<tr>" + "".join("<td>%s</td>" % _html_inline(c) for c in r) + "</tr>"
        for r in rows)
    return "<table>\n%s\n%s\n</table>" % (h, body)


def _cfg_rows():
    return [
        ("Стратегия", "`roc_momentum`: 5-дневная прибавка цены, n ∈ {5, 10, 20}, "
                      "порог 1%/2%/4%"),
        ("Сигнал", "на закрытии бара → вход по открытию следующего"),
        ("Направление", "**лонги и шорты**"),
        ("Плечо", "**%g×** — номинал позиции допускается до %g× наличных на счёте-субсчёте"
                  % (HEADLINE_LEV, HEADLINE_LEV)),
        ("Слоупок", "2·ATR (ATR, известный на момент решения)"),
        ("Тейк", "3·ATR"),
        ("Комиссия", "0,04% от номинала (без фиксированной платы)"),
        ("Размер позиции", "риск% от 100 тыс. ₽ субсчёта на сделку"),
        ("Оценка", "walk-forward: обучение 504 бара → тест 126 баров, "
                   "лучший параметр на обучении"),
        ("Бумаги", "40 (ORIG 25 + NEW 15), по 100 тыс. ₽ на бумагу, пул 4,0 млн ₽"),
    ]


def _md_method():
    return ("1. **Честный out-of-sample.** Параметры (n и порог) выбираются только на\n"
            "   обучающем окне 504 бара и применяются без изменений на следующие 126\n"
            "   баров. Каждая отдельная ячейка — свежие 100 тыс. ₽ с фиксированным пулом.\n"
            "2. **Нет заглядывания в будущее и нет невозможных сделок.** ATR для стопа и\n"
            "   размера позиции берётся с предыдущего закрытия (`atr[i-1]`), вход — только\n"
            "   по открытию следующего бара. Бар, на котором позиция закрылась, НЕ может\n"
            "   открыть новую: на его открытии позиция ещё была в рынке. Бар, открывшийся\n"
            "   за уровнем, исполняет стоп/тейк по открытию. OOS-сигнал И ATR\n"
            "   «прогреваются» реальной пред-фолдовой историей.\n"
            "3. **Фиксированный пул.** Результаты складываются по сделкам (не цепной\n"
            "   реинвест по фолдам — это взрывает до миллиардов и не является реальным\n"
            "   счётом). Пул на графиках = 4,0 млн ₽ (40 × 100 тыс.), кривая —\n"
            "   mark-to-market: открытые позиции переоцениваются по дневному закрытию,\n"
            "   поэтому просадка учитывает и «бумажный» минус. Открытая на границе фолда\n"
            "   позиция передаётся в следующий фолд, а не закрывается принудительно.\n"
            "4. **40 бумаг, все без исключения** (включая IMOEX как индекс-прокси),\n"
            "   данные MOEX `interval=24`, дневные бары.")


def _html_method():
    return """<ol>
<li><strong>Честный out-of-sample.</strong> Параметры (n и порог) выбираются только на обучающем окне 504 бара и применяются без изменений на следующие 126 баров. Каждая отдельная ячейка — свежие 100 тыс. ₽ с фиксированным пулом.</li>
<li><strong>Нет заглядывания в будущее и нет невозможных сделок.</strong> ATR для стопа и размера позиции берётся с предыдущего закрытия (<code>atr[i-1]</code>), вход — только по открытию следующего бара. Бар, на котором позиция закрылась, НЕ может открыть новую: на его открытии позиция ещё была в рынке. Бар, открывшийся за уровнем, исполняет стоп/тейк по открытию. OOS-сигнал И ATR «прогреваются» реальной пред-фолдовой историей.</li>
<li><strong>Фиксированный пул.</strong> Результаты складываются по сделкам (не цепной реинвест по фолдам — это взрывает до миллиардов и не является реальным счётом). Пул на графиках = 4,0 млн ₽ (40 × 100 тыс.), кривая — mark-to-market: открытые позиции переоцениваются по дневному закрытию, поэтому просадка учитывает и «бумажный» минус. Открытая на границе фолда позиция передаётся в следующий фолд, а не закрывается принудительно.</li>
<li><strong>40 бумаг, все без исключения</strong> (включая IMOEX как индекс-прокси), данные MOEX <code>interval=24</code>, дневные бары.</li>
</ol>"""


def build_reports(res, risk_df, lev_df, ye, bn, st40, lss, lsy, trades40, years):
    n40 = len(BASKET40)
    m = pool_metrics(res[("risk", HEADLINE_RISK, HEADLINE_LEV)][0],
                     trades40, n40, years)
    total, pool = m["total"], m["pool"]
    per_year = total / pool * 100 / years
    dd = m["pool_dd"]
    n_tr = m["n_trades"]
    eff_folds = m["pos_folds"] / max(m["n_fold"], 1) * 100
    names_pos = int((bn["pnl_RUB"] > 0).sum())
    years_pos = int((ye["pnl_RUB"] > 0).sum())
    n_years = len(ye)
    per_mo = n_tr / years / 12
    per_name_mo = n_tr / n40 / years / 12

    ts = pd.to_datetime(trades40["exit_time"])
    first = ts.min().strftime("%Y-%m")
    last = ts.max().strftime("%Y-%m")

    yd = {int(r["year"]): r for r in ye.to_dict("records")}
    lsyd = {int(r["year"]): r for r in lsy.to_dict("records")}
    # Bear / bull years are read off the index, never hardcoded: the package is
    # rebuilt on refreshed data and the year list changes with it.
    bear = [y for y, r in sorted(yd.items())
            if r["IMOEX_pct"] == r["IMOEX_pct"] and r["IMOEX_pct"] < 0]
    bull = [y for y, r in sorted(yd.items())
            if r["IMOEX_pct"] == r["IMOEX_pct"] and r["IMOEX_pct"] > 0]
    neg = [y for y, r in sorted(yd.items()) if r["pnl_RUB"] < 0]
    best_y = max(yd.items(), key=lambda kv: kv[1]["pnl_pct"])
    worst_y = min(yd.items(), key=lambda kv: kv[1]["pnl_pct"])

    def _yl(years_list):
        return ", ".join(str(y) for y in years_list) or "—"

    def _side_line(years_list):
        """'2022 (шорты +X, лонги +Y)' - the actual split, not an assertion."""
        parts = []
        for y in years_list:
            r = lsyd.get(y)
            if r is None:
                continue
            parts.append("%d (шорты %s, лонги %s)"
                         % (y, _pct(r["short_pct"]), _pct(r["long_pct"])))
        return "; ".join(parts) or "—"

    sh = lss[lss["direction"] == "short"].iloc[0] if len(lss) else None
    lg = lss[lss["direction"] == "long"].iloc[0] if len(lss) else None

    subtitle = ("40 бумаг · плечо %g× · комиссия 0,04%% · OOS walk-forward 504/126 · "
            "риск %g%%") % (HEADLINE_LEV, HEADLINE_RISK)
    summary = ("Портфельная стратегия **ROC-импульс на дневных барах** "
               "(40 бумаг MOEX, walk-forward 504/126, комиссия 0,04%%, "
               "стоп 2·ATR / тейк 3·ATR) с разрешёнными **шортами** и плечом "
               "**%g×**: номинал позиции допускается до %g× наличных суб-счёта.\n\n"
               "В годы падения индекса (%s) вклад сторон по пулу: %s. В годы роста "
               "(%s): %s.\n\n**Итог: %s из %s лет в плюсе, %s из %s бумаг "
               "прибыльны.**" % (HEADLINE_LEV, HEADLINE_LEV,
                                 _yl(bear), _side_line(bear), _yl(bull),
                                 _side_line(bull), years_pos, n_years,
                                 names_pos, n40))

    head_rows = [
        ("Совокупный результат OOS",
         "**%s = %s** на пул %s" % (_rub(total), _pct(total / pool * 100, 1),
                                    _fnum(pool / 1e6, 1) + " млн ₽")),
        ("Доходность", "**%s в год** (%s года)" % (_pct(per_year, 1), _fnum(years, 1))),
        ("Макс. просадка пула", "**%s**" % _pct(dd, 1)),
        ("Сделок", "%s (~%s в месяц по пулу, ~%s на бумагу)"
                  % (_fnum(n_tr), _fnum(per_mo), _fnum(per_name_mo, 1))),
        ("Доля прибыльных OOS-фолдов", "%s%% (%s из %s)"
                  % (_fnum(eff_folds, 1), _fnum(m["pos_folds"]), _fnum(m["n_fold"]))),
        ("Win rate по сделкам", _fnum(st40["win"], 1) + "%"),
        ("Средняя сделка", _rub(st40["avg"])),
        ("Profit Factor", _fnum(st40["pf"], 2)),
        ("Средний срок удержания", _fnum(st40["hold_days"], 1) + " дня"),
        ("Положительных бумаг", "**%s из %s**" % (_fnum(names_pos), _fnum(n40))),
        ("Положительных лет",
         "**%s из %s** (минус: %s)" % (_fnum(years_pos), _fnum(n_years), _yl(neg))),
    ]

    port_rows = [
        ("Совокупный OOS", "**%s млн ₽ (+%s%%)**"
                            % (_snum(total / 1e6, 2), _fnum(total / pool * 100, 0))),
        ("В год", "**%s**" % _pct(per_year, 1)),
        ("Макс. просадка пула", "**%s**" % _pct(dd, 1)),
        ("Сделок", _fnum(n_tr)),
        ("Win rate", _fnum(st40["win"], 1) + "%"),
        ("Средняя сделка", _rub(st40["avg"])),
        ("Profit Factor", _fnum(st40["pf"], 2)),
        ("Средний удерж.", _fnum(st40["hold_days"], 1) + " дн"),
        ("Средний выигрыш/проигрыш", "%s / %s" % (_rub(st40["avg_w"]), _rub(st40["avg_l"]))),
        ("Положительных бумаг", "**%s/%s**" % (_fnum(names_pos), _fnum(n40))),
        ("Положительных лет", "**%s/%s**" % (_fnum(years_pos), _fnum(n_years))),
    ]

    ss = lss["n"].sum()
    sp = lss.loc[lss["direction"] == "short", "total"].sum()
    lp = lss.loc[lss["direction"] == "long", "total"].sum()
    sp_share = sp / (sp + lp) * 100

    def fs(row):
        return "%s (%s%%)" % (_fnum(row["n"]),
                              _fnum(row["n"] / ss * 100, 1))

    ls_rows = [
        ("Сделок", fs(sh), fs(lg)),
        ("Совокупный PnL",
         "**%s (%s%% прибыли)**" % (_rub(sh["total"]), _fnum(sp_share)),
         "%s (%s%%)" % (_rub(lg["total"]), _fnum(100 - sp_share))),
        ("Win rate", _fnum(sh["win"], 1) + "%", _fnum(lg["win"], 1) + "%"),
        ("Средняя сделка", _rub(sh["avg"]), _rub(lg["avg"])),
        ("Profit Factor", _fnum(sh["pf"], 2), _fnum(lg["pf"], 2)),
        ("Средний удерж.", _fnum(sh["hold_days"], 1) + " дн",
         _fnum(lg["hold_days"], 1) + " дн"),
    ]

    risk_rows = []
    for r in risk_df.to_dict("records"):
        cells = ["%s%%" % _fnum(r["risk%"]), _pct(r["per_year_%"]),
                 _pct(r["pool_maxDD_%"]), _fnum(r["trades_total"]),
                 _rub(r["avg_pnl_per_trade"])]
        if r["risk%"] == HEADLINE_RISK:
            cells = ["**" + c + "**" for c in cells]
        risk_rows.append(cells)

    lev_rows = []
    for r in lev_df.to_dict("records"):
        cells = [_fnum(r["leverage"]) + "×", _pct(r["per_year_%"]),
                 _pct(r["pool_maxDD_%"]), _rub(r["avg_pnl_per_trade"])]
        if r["leverage"] == HEADLINE_LEV:
            cells = ["**" + c + "**" for c in cells]
        lev_rows.append(cells)

    year_rows = []
    for t in ye.to_dict("records"):
        year_rows.append([
            "%s%s" % (t["year"], "*" if t.get("partial") else ""),
            _rub(t["pnl_RUB"]), _pct(t["pnl_pct"]),
            _snum(t["IMOEX_pct"], 1) if t["IMOEX_pct"] == t["IMOEX_pct"] else "—",
            _fnum(t["trades"]), _fnum(t["win_pct"], 1),
            _snum(t["avg_trade"])])

    sig_parts = []
    bn_index = bn.set_index("ticker")
    for tk in SIGNAL_TICKERS:
        row = bn_index.loc[tk]
        pct100 = _pct(row["pnl_pct_100k"])
        met = ("PnL **%s** (%s к 100 тыс.) · сделок %s · лонги %s / шорты %s · "
               "maxDD %s" % (_rub(row["pnl_RUB"]), pct100, _fnum(row["trades"]),
                             _rub(row["long_pnl"]), _rub(row["short_pnl"]),
                             "-%s%%" % _fnum(abs(row["maxDD_pct"]), 1)))
        sig_parts.append((tk, met))

    app_rows = []
    for t in bn.to_dict("records"):
        app_rows.append([
            t["ticker"], _rub(t["pnl_RUB"]), _snum(t["pnl_pct_100k"], 1),
            _fnum(t["trades"]), _rub(t["long_pnl"]), _rub(t["short_pnl"]),
            "-%s%%" % _fnum(abs(t["maxDD_pct"]), 1)])

    # ------------------------------ markdown ------------------------------
    md = []
    md.append("# Портфельная стратегия ROC-импульс на дневных барах MOEX — **лонги и шорты**")
    md.append("")
    md.append("**%s**" % subtitle)
    md.append("")
    md.append(_md_table(["Метрика", "Значение"], [[a, b] for a, b in head_rows]))
    md.append("")
    md.append("---")
    md.append("")
    md.append("## 1. Резюме")
    md.append("")
    md.append(summary)
    md.append("")
    md.append("---")
    md.append("")
    md.append("## 2. Конфигурация")
    md.append("")
    md.append(_md_table(["Параметр", "Значение"], [[a, b] for a, b in _cfg_rows()]))
    md.append("")
    p1 = "| Период OOS | %s … %s (%s года OOS) |" % (first, last, _fnum(years, 1))
    md.append(p1)
    md.append("")
    md.append("---")
    md.append("")
    md.append("## 3. Методология")
    md.append("")
    md.append(_md_method())
    md.append("")
    md.append("---")
    md.append("")
    md.append("## 4. Портфель в цифрах (лонги и шорты, плечо %g×)" % HEADLINE_LEV)
    md.append("")
    md.append(_md_table(["Метрика", "**Лонги + шорты (лев %g×)**" % HEADLINE_LEV],
                        [[a, b] for a, b in port_rows]))
    md.append("")
    md.append("PF: шорты %s, лонги %s. При плече %g× суммарный номинал позиций "
              "может достигать %g× наличных. Учтите §9: дивидендные гэпы не "
              "смоделированы и смещают эту разбивку в пользу шортов."
              % (_fnum(sh["pf"], 2), _fnum(lg["pf"], 2),
                 HEADLINE_LEV, HEADLINE_LEV))
    md.append("")
    md.append("![Кривая средств](images/equity_pooled.png)")
    md.append("")
    md.append("![Индивидуальные кривые бумаг](images/equity_pername.png)")
    md.append("")
    md.append("![Вклад бумаг](images/pername_contribution.png)")
    md.append("")
    md.append("---")
    md.append("")
    md.append("## 5. Лонги vs шорты: кто зарабатывает")
    md.append("")
    md.append(_md_table(["", "**Шорты**", "**Лонги**"], ls_rows))
    md.append("")
    md.append("Разбивка по годам — в `tables/long_short_by_year.csv`. В годы "
              "падения индекса стороны дали: %s." % _side_line(bear))
    md.append("")
    md.append("![Распределение PnL: лонги vs шорты](images/pnl_histogram.png)")
    md.append("")
    md.append("---")
    md.append("")
    md.append("## 6. Риск → доходность (плечо %g×)" % HEADLINE_LEV)
    md.append("")
    md.append(_md_table(["Риск на сделку", "% в год", "Просадка пула",
                         "Сделок", "Средняя сделка"], risk_rows))
    md.append("")
    md.append("Доходность растёт быстрее на малом риске и выходит на плато при "
              "крупных рисках (номинал упирается в лимит плеча, растёт только "
              "просадка). Рабочая зона — 5–10%.")
    md.append("")
    md.append("![Риск → доходность: лонги+шорты (плечо %g×)](images/risk_return.png)"
              % HEADLINE_LEV)
    md.append("")
    md.append("---")
    md.append("")
    md.append("## 7. Плечо → доходность (риск %g%% на сделку)" % HEADLINE_RISK)
    md.append("")
    md.append("Рабочее плечо — **%g×** (строка выделена жирным). Доходность растёт "
              "почти пропорционально плечу, но растёт и просадка пула; реальные "
              "издержки маржинального финансирования лонгов и платы за заём "
              "шорт-ног в бэктесте не учтены и съедают плечевую надбавку (см. §9)."
              % HEADLINE_LEV)
    md.append("")
    md.append(_md_table(["Плечо", "% в год", "Просадка пула", "Средняя сделка"],
                        lev_rows))
    md.append("")
    md.append("![Плечо → доходность](images/leverage_return.png)")
    md.append("")
    md.append("---")
    md.append("")
    md.append("## 8. Доходность по годам")
    md.append("")
    md.append(_md_table(["Год", "PnL, ₽", "% (пул 4,0 млн)", "IMOEX, %",
                         "Сделок", "Win, %", "Средняя"], year_rows))
    md.append("")
    md.append("Отрицательные годы: %s. В годы падения индекса (%s) результат "
              "стратегии по пулу: %s. Годы, помеченные *, покрыты OOS-окном "
              "не полностью — их процент относится к части года, а не к году "
              "целиком."
              % (", ".join("%d (%s)" % (y, _pct(yd[y]["pnl_pct"])) for y in neg)
                 or "нет",
                 _yl(bear),
                 "; ".join("%d %s" % (y, _pct(yd[y]["pnl_pct"])) for y in bear)))
    md.append("")
    md.append("![По годам vs IMOEX](images/returns_by_year.png)")
    md.append("")
    md.append("![Прибыль по годам](images/pnl_by_year.png)")
    md.append("")
    md.append("---")
    md.append("")
    md.append("## 9. Риски и оговорки (обязательно к прочтению)")
    md.append("")
    md.append(MD_RISKS)
    md.append("")
    md.append("---")
    md.append("")
    md.append("## 10. Вывод")
    md.append("")
    md.append("Лучший год — %d (%s), худший — %d (%s); %s из %s лет в плюсе, "
              "%s из %s бумаг в плюсе. PF по шортам %s, по лонгам %s."
              % (best_y[0], _pct(best_y[1]["pnl_pct"]),
                 worst_y[0], _pct(worst_y[1]["pnl_pct"]),
                 years_pos, n_years, names_pos, n40,
                 _fnum(sh["pf"], 2), _fnum(lg["pf"], 2)))
    md.append("")
    md.append("Шорты исполняются фьючерсами на соответствующую бумагу (без платы "
              "за заём и ночной перенос, см. §9 п.5), поэтому главная из "
              "несмоделированных издержек — плата за заёмный шорт — на уровне "
              "исполнения не возникает; остаются несмоделированными квартальный "
              "ролл фьючерса и гэпы. Для внедрения рекомендуется начать с "
              "небольшого риска (5–8%) и измерить фактическое исполнение "
              "шорт-ноги на живом счёте.")
    md.append("")
    md.append("---")
    md.append("")
    md.append("## 11. Графики цен с сигналами (примеры: SBER, GAZP, ALRS, LKOH, "
              "ROSN, NLMK, CHMF, TATN, полная история)")
    md.append("")
    md.append(("Для иллюстрации работы сигналов на реальных котировках показаны "
               "дневные свечи (OHLC) восьми бумаг корзины за **всю доступную "
               "историю** (начиная с первого бара данных, 2015–2017 → август %s) "
               "с нанесёнными входами и выходами стратегии. Это **настоящие "
               "OOS-сделки** из walk-forward прогонов (риск %g%%, плечо %g×, "
               "лонги+шорты), попавшие в окно графика, а не переобученные на этом "
               "участке сигналы. Графики очень широкие — при просмотре их нужно "
               "прокручивать по горизонтали."
               % (last[:4], HEADLINE_RISK, HEADLINE_LEV)))
    md.append("")
    md.append("**Легенда (одинакова для всех восьми графиков):**")
    md.append("")
    md.append("- **Синий ▲** — BUY: открытие лонга или закрытие шорта")
    md.append("- **Оранжевый ▼** — SELL: открытие шорта или закрытие лонга")
    md.append("- Серые линии соединяют вход и выход одной сделки")
    md.append("- **Свечи: зелёная — рост дня, красная — падение дня** "
              "(тело = открытие/закрытие, сегмент = диапазон день)")
    md.append("- Рядом с каждым выходом — **подпись с PnL сделки** в ₽ "
              "(синяя = прибыль, оранжевая = убыток)")
    md.append("- **Нижняя панель** каждого графика — кривая средств суб-счёта "
              "(старт 100 тыс. ₽) **mark-to-market**: реализованный PnL по "
              "закрытым сделкам плюс ежедневная переоценка открытых позиций по "
              "дневной цене закрытия")
    md.append("")
    for tk, met in sig_parts:
        md.append(met)
        md.append("")
        md.append('<img src="images/signals_%s.png" width="2800">' % tk.lower())
        md.append("")
    md.append("Примерный календарь событий по графику SBER: летом 2026 г. на "
              "фоне падения рынка работают преимущественно короткие сигналы (▼), "
              "в периоды отскоков — покупки (▲). PnL в заголовке каждого графика "
              "— суммарный результат по закрытым в окне сделкам (за всю историю "
              "совпадает с итогами из таблицы приложения); отрицательный PnL по "
              "какой-то одной бумаге в коротком окне является нормальной "
              "дисперсией: доходность даёт корзина из 40 имён, а не отдельная "
              "позиция.")
    md.append("")
    md.append("---")
    md.append("")
    md.append("## Приложение. Все 40 бумаг (OOS, риск %g%%, лонги+шорты, плечо %g×)"
              % (HEADLINE_RISK, HEADLINE_LEV))
    md.append("")
    md.append(_md_table(["Бумага", "PnL, ₽", "% на 100 тыс.", "Сделок",
                         "Лонги, ₽", "Шорты, ₽", "Просадка, %"], app_rows))
    md.append("")
    md.append("*Пакет полностью пересобирается: `python report_build.py` "
              "(прогоны кэшируются в `tables/_cache`). Исходник данных — "
              "`data/candles_ru_daily/`.*")
    md.append("")

    # ------------------------------ html ------------------------------
    h = []
    h.append("<!DOCTYPE html>")
    h.append('<html lang="ru">')
    h.append("<head>")
    h.append('<meta charset="utf-8">')
    h.append('<meta name="viewport" content="width=device-width, initial-scale=1">')
    h.append("<title>Портфельная стратегия ROC-импульс на дневных барах MOEX — "
             "лонги и шорты</title>")
    h.append("<style>")
    h.append(HTML_CSS)
    h.append("</style>")
    h.append("</head>")
    h.append("<body>")
    h.append("")
    h.append("<h1>Портфельная стратегия ROC-импульс на дневных барах MOEX — "
             "<strong>лонги и шорты</strong></h1>")
    h.append("")
    h.append("<p><strong>%s</strong></p>" % subtitle)
    h.append("")
    h.append(_html_table(["Метрика", "Значение"], [[a, b] for a, b in head_rows]))
    h.append("")
    h.append("<hr>")
    h.append("")
    h.append("<h2>1. Резюме</h2>")
    h.append("<p>%s</p>" % _html_inline(summary.replace("\n\n", "</p>\n<p>")))
    h.append("")
    h.append("<hr>")
    h.append("")
    h.append("<h2>2. Конфигурация</h2>")
    cfg_tbl_rows = [[a, b] for a, b in _cfg_rows()]
    cfg_tbl_rows.append(["Период OOS", "%s … %s (%s года OOS)"
                         % (first, last, _fnum(years, 1))])
    h.append(_html_table(["Параметр", "Значение"], cfg_tbl_rows))
    h.append("")
    h.append("<hr>")
    h.append("")
    h.append("<h2>3. Методология</h2>")
    h.append(_html_method())
    h.append("")
    h.append("<hr>")
    h.append("")
    h.append("<h2>4. Портфель в цифрах (лонги и шорты, плечо %g×)</h2>" % HEADLINE_LEV)
    h.append(_html_table(["Метрика", "**Лонги + шорты (лев %g×)**" % HEADLINE_LEV],
                         [[a, b] for a, b in port_rows]))
    h.append("<p>PF: шорты %s, лонги %s. При плече %g× суммарный номинал позиций "
             "может достигать %g× наличных. Учтите §9: дивидендные гэпы не "
             "смоделированы и смещают эту разбивку в пользу шортов.</p>"
             % (_fnum(sh["pf"], 2), _fnum(lg["pf"], 2),
                HEADLINE_LEV, HEADLINE_LEV))
    h.append("")
    h.append('<img class="normal" src="images/equity_pooled.png" alt="Кривая средств">')
    h.append('<img class="normal" src="images/equity_pername.png" '
             'alt="Индивидуальные кривые бумаг">')
    h.append('<img class="normal" src="images/pername_contribution.png" '
             'alt="Вклад бумаг">')
    h.append("")
    h.append("<hr>")
    h.append("")
    h.append("<h2>5. Лонги vs шорты: кто зарабатывает</h2>")
    h.append(_html_table(["", "**Шорты**", "**Лонги**"], ls_rows))
    h.append("<p>Разбивка по годам — в <code>tables/long_short_by_year.csv</code>. "
             "В годы падения индекса стороны дали: %s.</p>" % _html_inline(_side_line(bear)))
    h.append("")
    h.append('<img class="normal" src="images/pnl_histogram.png" '
             'alt="Распределение PnL: лонги vs шорты">')
    h.append("")
    h.append("<hr>")
    h.append("")
    h.append("<h2>6. Риск → доходность (плечо %g×)</h2>" % HEADLINE_LEV)
    h.append(_html_table(["Риск на сделку", "% в год", "Просадка пула",
                          "Сделок", "Средняя сделка"], risk_rows))
    h.append("<p>Доходность растёт быстрее на малом риске и выходит на плато при "
             "крупных рисках (номинал упирается в лимит плеча, растёт только "
             "просадка). Рабочая зона — 5–10%.</p>")
    h.append("")
    h.append('<img class="normal" src="images/risk_return.png" '
             'alt="Риск → доходность: лонги+шорты (плечо %g×)">' % HEADLINE_LEV)
    h.append("")
    h.append("<hr>")
    h.append("")
    h.append("<h2>7. Плечо → доходность (риск %g%% на сделку)</h2>" % HEADLINE_RISK)
    h.append("<p>Рабочее плечо — <strong>%g×</strong> (строка выделена жирным). "
             "Доходность растёт почти пропорционально плечу, но растёт и просадка "
             "пула; реальные издержки маржинального финансирования лонгов и платы "
             "за заём шорт-ног в бэктесте не учтены и съедают плечевую надбавку "
             "(см. §9).</p>" % HEADLINE_LEV)
    h.append(_html_table(["Плечо", "% в год", "Просадка пула", "Средняя сделка"],
                         lev_rows))
    h.append("")
    h.append('<img class="normal" src="images/leverage_return.png" '
             'alt="Плечо → доходность">')
    h.append("")
    h.append("<hr>")
    h.append("")
    h.append("<h2>8. Доходность по годам</h2>")
    h.append(_html_table(["Год", "PnL, ₽", "% (пул 4,0 млн)", "IMOEX, %",
                          "Сделок", "Win, %", "Средняя"], year_rows))
    h.append("<p>Отрицательные годы: %s. В годы падения индекса (%s) результат "
             "стратегии по пулу: %s. Годы, помеченные *, покрыты OOS-окном не "
             "полностью — их процент относится к части года, а не к году целиком.</p>"
             % (", ".join("%d (%s)" % (y, _pct(yd[y]["pnl_pct"])) for y in neg)
                or "нет",
                _yl(bear),
                "; ".join("%d %s" % (y, _pct(yd[y]["pnl_pct"])) for y in bear)))
    h.append("")
    h.append('<img class="normal" src="images/returns_by_year.png" '
             'alt="По годам vs IMOEX">')
    h.append('<img class="normal" src="images/pnl_by_year.png" '
             'alt="Прибыль по годам">')
    h.append("")
    h.append("<hr>")
    h.append("")
    h.append("<h2>9. Риски и оговорки (обязательно к прочтению)</h2>")
    h.append(HTML_RISKS)
    h.append("")
    h.append("<hr>")
    h.append("")
    h.append("<h2>10. Вывод</h2>")
    h.append("<p>Лучший год — %d (%s), худший — %d (%s); %s из %s лет в плюсе, "
             "%s из %s бумаг в плюсе. PF по шортам %s, по лонгам %s.</p>"
             % (best_y[0], _pct(best_y[1]["pnl_pct"]),
                worst_y[0], _pct(worst_y[1]["pnl_pct"]),
                years_pos, n_years, names_pos, n40,
                _fnum(sh["pf"], 2), _fnum(lg["pf"], 2)))
    h.append("<p>Шорты исполняются фьючерсами на соответствующую бумагу (без "
             "платы за заём и ночной перенос, см. §9 п.5), поэтому главная из "
             "несмоделированных издержек — плата за заёмный шорт — на уровне "
             "исполнения не возникает; остаются несмоделированными квартальный "
             "ролл фьючерса и гэпы. Для внедрения рекомендуется начать с "
             "небольшого риска (5–8%) и измерить фактическое исполнение "
             "шорт-ноги на живом счёте.</p>")
    h.append("")
    h.append("<hr>")
    h.append("")
    h.append("<h2>11. Графики цен с сигналами (примеры: SBER, GAZP, ALRS, LKOH, "
             "ROSN, NLMK, CHMF, TATN, полная история)</h2>")
    h.append(("<p>Для иллюстрации работы сигналов на реальных котировках показаны "
              "дневные свечи (OHLC) восьми бумаг корзины за <strong>всю доступную "
              "историю</strong> (начиная с первого бара данных, 2015–2017 → август "
"%s) с нанесёнными входами и выходами стратегии. Это "
               "<strong>настоящие OOS-сделки</strong> из walk-forward прогонов "
               "(риск %g%%, плечо %g×, лонги+шорты), попавшие в окно графика, а не "
               "переобученные на этом участке сигналы. Графики очень широкие — "
               "<strong>прокручиваются по горизонтали</strong> внутри рамки.</p>"
               % (last[:4], HEADLINE_RISK, HEADLINE_LEV)))
    h.append("")
    h.append("<p><strong>Легенда (одинакова для всех восьми графиков):</strong></p>")
    h.append("<ul>")
    h.append("<li><strong>Синий ▲</strong> — BUY: открытие лонга или закрытие шорта</li>")
    h.append("<li><strong>Оранжевый ▼</strong> — SELL: открытие шорта или закрытие лонга</li>")
    h.append("<li>Серые линии соединяют вход и выход одной сделки</li>")
    h.append("<li><strong>Свечи: зелёная — рост дня, красная — падение дня</strong> "
             "(тело = открытие/закрытие, сегмент = диапазон день)</li>")
    h.append("<li>Рядом с каждым выходом — <strong>подпись с PnL сделки</strong> в ₽ "
             "(синяя = прибыль, оранжевая = убыток)</li>")
    h.append("<li><strong>Нижняя панель</strong> каждого графика — кривая средств "
             "суб-счёта (старт 100 тыс. ₽) <strong>mark-to-market</strong>: "
             "реализованный PnL по закрытым сделкам плюс ежедневная переоценка "
             "открытых позиций по дневной цене закрытия</li>")
    h.append("</ul>")
    h.append("")
    for tk, met in sig_parts:
        h.append(('<p class="signal-name"><strong>%s — цена и сигналы (полная '
                  'история)</strong> <span class="hint">— для просмотра '
                  'прокручивайте изображение влево/вправо</span></p>' % tk))
        h.append('<p class="metrics">%s</p>' % _html_inline(met))
        h.append('<div class="scroll"><img src="images/signals_%s.png" '
                 'alt="%s — цена и сигналы"></div>' % (tk.lower(), tk))
    h.append("")
    h.append("<p>Примерный календарь событий по графику SBER: летом 2026 г. на "
             "фоне падения рынка работают преимущественно короткие сигналы (▼), "
             "в периоды отскоков — покупки (▲). PnL в заголовке каждого графика "
             "— суммарный результат по закрытым в окне сделкам (за всю историю "
             "совпадает с итогами из таблицы приложения); отрицательный PnL по "
             "какой-то одной бумаге в коротком окне является нормальной "
             "дисперсией: доходность даёт корзина из 40 имён, а не отдельная "
             "позиция.</p>")
    h.append("")
    h.append("<hr>")
    h.append("")
    h.append("<h2>Приложение. Все 40 бумаг (OOS, риск %g%%, лонги+шорты, плечо %g×)</h2>"
             % (HEADLINE_RISK, HEADLINE_LEV))
    h.append(_html_table(["Бумага", "PnL, ₽", "% на 100 тыс.", "Сделок",
                          "Лонги, ₽", "Шорты, ₽", "Просадка, %"], app_rows))
    h.append("<p><em>Пакет полностью пересобирается: <code>python report_build.py</code> "
             "(прогоны кэшируются в <code>tables/_cache</code>). Исходник данных — "
             "<code>data/candles_ru_daily/</code>.</em></p>")
    h.append("")
    h.append("</body>")
    h.append("</html>")

    (OUT / "report.md").write_text("\n".join(md) + "\n", encoding="utf-8")
    (OUT / "report.html").write_text("\n".join(h) + "\n", encoding="utf-8")
    print("documents -> report/report.md, report/report.html")


def save_tables(res, trades40, years, tr25):
    TBL.mkdir(parents=True, exist_ok=True)
    n40 = len(BASKET40)

    risk_rows = []
    for k, (folds, trades, nbars) in res.items():
        kind, risk, lev = k
        if kind == "risk":
            m = pool_metrics(folds, trades, n40, years)
            risk_rows.append({
                "risk%": risk, "OOS_total_M": m["total"] / 1e6,
                "per_year_%": m["total"] / m["pool"] * 100 / years,
                "pool_maxDD_%": m["pool_dd"],
                "pos_folds_%": m["pos_folds"] / max(m["n_fold"], 1) * 100,
                "avg_fold_DD_%": m["avg_fold_dd"],
                "trades_total": m["n_trades"],
                "avg_pnl_per_trade": m["total"] / max(m["n_trades"], 1)})
            print("RISK %2d%%: total %+.2fM (%+.1f%%/yr), pool DD %.1f%%, "
                  "pos %d/%d, %d trades, avg %+.0f RUB"
                  % (risk, m["total"] / 1e6,
                     m["total"] / m["pool"] * 100 / years, m["pool_dd"],
                     m["pos_folds"], m["n_fold"], m["n_trades"],
                     m["total"] / max(m["n_trades"], 1)))
    risk_df = pd.DataFrame(risk_rows)
    risk_df.to_csv(TBL / "risk_scan.csv", index=False, float_format="%.2f")

    lev_rows = []
    for k, (folds, trades, nbars) in res.items():
        kind, risk, levv = k
        if kind == "lev":
            m = pool_metrics(folds, trades, n40, years)
            lev_rows.append({
                "leverage": levv, "OOS_total_M": m["total"] / 1e6,
                "per_year_%": m["total"] / m["pool"] * 100 / years,
                "pool_maxDD_%": m["pool_dd"],
                "pos_folds_%": m["pos_folds"] / max(m["n_fold"], 1) * 100,
                "avg_fold_DD_%": m["avg_fold_dd"],
                "trades_total": m["n_trades"],
                "avg_pnl_per_trade": m["total"] / max(m["n_trades"], 1)})
            print("LEV %2gx : total %+.2fM (%+.1f%%/yr), pool DD %.1f%%, "
                  "pos %d/%d, %d trades, avg %+.0f RUB"
                  % (levv, m["total"] / 1e6,
                     m["total"] / m["pool"] * 100 / years, m["pool_dd"],
                     m["pos_folds"], m["n_fold"], m["n_trades"],
                     m["total"] / max(m["n_trades"], 1)))
    lev_df = pd.DataFrame(lev_rows)
    lev_df.to_csv(TBL / "leverage_scan.csv", index=False, float_format="%.2f")

    ye = pct_year(trades40, n40)
    # benchmark measured over the same window the strategy is measured on
    ye["IMOEX_pct"] = ye["year"].map(
        moex_year_returns(pd.to_datetime(trades40["exit_time"]).min()))
    ye.to_csv(TBL / "by_year.csv", index=False, float_format="%.2f")

    bn = by_name(trades40)
    bn.to_csv(TBL / "by_name.csv", index=False, float_format="%.2f")

    st40 = trade_stats(trades40)
    m = pool_metrics(res[("risk", HEADLINE_RISK, HEADLINE_LEV)][0],
                     trades40, n40, years)
    st_head = dict(st40, **{"total_pct": m["total"] / (POOL * n40) * 100,
                            "OOS_total_M": m["total"] / 1e6})
    rows_stats = [dict(variant="40_names_L+S_lev%d" % HEADLINE_LEV, **st_head)]
    if tr25 is not None and len(tr25):
        rows_stats.append(dict(variant="25_names_L+S_lev%d" % HEADLINE_LEV,
                               **trade_stats(tr25)))
    pd.DataFrame(rows_stats).to_csv(TBL / "trade_stats.csv", index=False,
                                    float_format="%.2f")

    l = trades40.loc[trades40["dir"] == 1]
    sh = trades40.loc[trades40["dir"] == -1]
    lss = pd.DataFrame([dict(direction="short", **trade_stats(sh)),
                        dict(direction="long", **trade_stats(l))])
    lss["share_n_pct"] = lss["n"] / len(trades40) * 100
    lss.to_csv(TBL / "long_short_split.csv", index=False, float_format="%.2f")

    lsy = ls_by_year(trades40, n40)
    lsy.to_csv(TBL / "long_short_by_year.csv", index=False, float_format="%.2f")

    if tr25 is not None and len(tr25):
        ye25 = pct_year(tr25, 25)
        mm = ye[["year", "pnl_RUB"]].rename(columns={"pnl_RUB": "pnl_40"})
        mm = mm.merge(ye25[["year", "pnl_RUB"]].rename(
            columns={"pnl_RUB": "pnl_25"}), on="year")
        mm["delta_new"] = mm["pnl_40"] - mm["pnl_25"]
        mm.to_csv(TBL / "delta_new_names.csv", index=False, float_format="%.0f")

    _daily_pnl(trades40).rename("pnl").to_csv(
        TBL / "equity_pooled_pnl.csv", float_format="%.0f")

    return risk_df, lev_df, ye, bn, st40, lss, lsy


def main():
    global USE_CACHE
    if "--no-cache" in sys.argv[1:]:
        USE_CACHE = False
        print("cache disabled: every walk-forward is recomputed")
    IMG.mkdir(parents=True, exist_ok=True)
    TBL.mkdir(parents=True, exist_ok=True)
    style()

    res = {}
    for risk in RISKS:
        k = ("risk", risk, HEADLINE_LEV)
        got = get(risk, HEADLINE_LEV, BASKET40)
        res[k] = (got["folds"], got["trades"], got["nbars"])
    for lev in LEVS:
        k = ("lev", HEADLINE_RISK, lev)
        got = get(HEADLINE_RISK, lev, BASKET40)
        res[k] = (got["folds"], got["trades"], got["nbars"])

    trades40 = res[("risk", HEADLINE_RISK, HEADLINE_LEV)][1]
    tr25 = get(HEADLINE_RISK, HEADLINE_LEV, ORIG)["trades"]
    nbits = max(v[2] for v in res.values())
    # OOS coverage = every fold that actually ran, i.e. floor((n - train)/oos)
    # windows of oos_days each. The old formula subtracted one extra oos window
    # and shortened the denominator by half a year (22.1%/yr instead of 21.2%).
    n_folds = max(0, (nbits - TRAIN_BARS) // OOS_BARS)
    years = n_folds * OOS_BARS / BARS_PER_YEAR

    print("tabulating ...")
    risk_df, lev_df, ye, bn, st40, lss, lsy = save_tables(
        res, trades40, years, tr25)
    print("charting ...")
    chart_pooled(trades40, len(BASKET40), HEADLINE_RISK)
    chart_pername(trades40)
    chart_contribution(trades40)
    chart_year_bars(ye)
    chart_year_money(ye)
    chart_hist(trades40)
    for tk in SIGNAL_TICKERS:
        chart_signals(tk, trades40)
    chart_risk_return(risk_df)
    chart_leverage(lev_df)
    print("writing report.md / report.html ...")
    build_reports(res, risk_df, lev_df, ye, bn, st40, lss, lsy, trades40, years)
    print("done ->", OUT)


if __name__ == "__main__":
    main()