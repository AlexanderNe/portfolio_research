# TECHNICAL.md - technical specification for `moex-roc-basket-long-short`

This doc is for the next engineer/researcher (or agent): how to rebuild, verify,
and not break this package. The user-facing overview is in `README.md`.

> [!WARNING]
> **The headline numbers below are stale and must not be quoted.**
> They come from a simulator that allowed a bar which had just stopped a position
> out to also open a new one *at that bar's open* - a fill that no longer exists by
> the time the stop is hit. On SBER/GAZP/LKOH that was 69% of all trades and 77% of
> the PnL. Fixing it, together with fold-boundary exits, an un-warmed fold-local ATR
> and stops that assumed the level was always available, takes an 8-name walk-forward
> from **+225% to +42%** over the same 9.5 years, and 3 of those 8 names turn
> negative.
>
> The simulator is fixed on this branch; `report/` has **not** been rebuilt on it.
> Rebuild with `python report_build.py --no-cache` before quoting anything here.
> Full analysis: [`CODE_REVIEW.md`](CODE_REVIEW.md).

---

## 1. Status and verdict

- Strategy: a daily **ROC-momentum basket** on 40 liquid MOEX stocks,
  **long + short, leverage = 1** (no borrowed money). This is the only
  configuration in this repository that can be called validated.
- History: the `roc_momentum` algorithm was previously verified as "longs+shorts
  at 7x leverage" (25 names) and as "longs without leverage" (40 names).
  2026-09-05g: shorts are allowed, leverage is forbidden -> this package
  (40 names, lev 1, L+S). **Do not bring leverage back** without an explicit
  request - margin/short-borrow financing is not modeled, so the numbers would be
  illusory.
- The IMOEXF daily swing-trading idea and all intraday (no-overnight) variants
  were tested and declared NOT viable - they are not part of this package.

## 2. Results (re-verify, don't paraphrase loosely)

Headline (risk 10%, pool 4.0M RUB = 40 x 100k, walk-forward 504/126,
comm 0.04%, fixed pool):

| Metric | Value |
|---|---|
| OOS total | **+8,043,568 RUB = +201.1% = +22.1%/yr** |
| Pool maxDD | -9.3% |
| Trades | 7,192 |
| Win | 50.2% |
| Avg/trade | +1,118 RUB |
| PF | 1.40 |
| Hold | 16.2 days |
| Positive folds | 64.7% |
| Names positive | 40/40 |
| Years positive | 9/10 (only 2018 = -3.4%) |

Long/short split: shorts 3,379 trades (47%) = +4,291,635 RUB, win 49.8%,
avg +1,270, PF 1.41; longs 3,813 trades = +3,751,932 RUB, win 50.5%, avg +984,
PF 1.39. Both sides independently profitable.

Risk scan (leverage 1, saturates ~10%): 1% +4.0%/-1.4%; 5% +18.2%/-5.3%;
10% +22.1%/-9.3%; 15% +22.0%/-12.5%.

Reference only (leverage FORBIDDEN, shown to understand the ceiling): 2x
+39.3%/-9.0%; 3x +46.2%/-11.6%; 5-10x ~+47.5%/-13%.

## 3. Parameters and mechanics

### Execution model (fixed 2026-09-12 - do not revert)

- A bar that produced an **exit** may **not** also produce an **entry**. The
  position was still open at that bar's open, so filling a new one there is a
  fill in the past. This was the single largest error in the package.
- A position open at a **fold boundary** is handed to the next fold
  (`cfg["carry_positions"]`, default on) instead of being force-closed at the
  last close; the boundary is an artefact of slicing, not a trading rule. The
  next fold still starts from the nominal capital, adjusted for the cash the
  carried position already consumed.
- The OOS simulator's **ATR is warmed** with pre-fold history exactly like the
  signal. A fold-local EWM re-seeds on the fold's first bar and stays biased for
  roughly `3*period` bars, which distorts both stop distance and sizing.
- A bar that **opens beyond** a stop/target fills at the OPEN: stops gap through,
  targets gap into.
- The leverage cap leaves room for the **opening commission**, so
  `notional <= cash` holds exactly.
- Optional: `--lot` rounds size down to whole exchange lots, `--slippage` applies
  adverse slippage to every fill. Both default to off/1 so they do not silently
  change historical comparisons.
- Parameter selection is **fee-aware by default** (`--fee-blind-selection`
  restores the old gross selection).

- `strat_roc_momentum(df, n, threshold)`: signal at the **close of bar i**:
  `ROC = close/close[i-n] - 1`; `ROC > +thr` -> +1, `ROC < -thr` -> -1, else 0.
  This package uses n=5, thr=0.01 (per-fold selection from the grid inside
  walk-forward).
- Execution: entry at the **next bar's open**, at most one position per name.
- Exit: SL 2*ATR / TP 3*ATR (`sl_atr=2.0, tp_atr=3.0, exit_grid=[(2.0, 3.0)]`),
  `trail_grid=(0.0,)` - **do not set `trail_grid=[]`** (breaks the plot scripts).
- Sizing (`fixed_units=0`): `units = int(risk_pct*equity / sl_dist)`, capped at
  `int(leverage*cash / notional_rub)`. With leverage 1 and risk >= ~8% the
  position saturates to "notional = cash" - beyond that only the drawdown grows.
- Commission `commission_pct=0.04` (% of turnover), `point_rub=1.0`,
  `fixed_fee=0.0`, `day_stop` off, `same_day_exit=False`.
- Walk-forward: `walk_forward(train_days=504, oos_days=126, min_trades=3)`.
  The OOS signal is **warmed** with real pre-fold history
  (`warm = min(500, len(train))`, `pd.concat([train.tail(warm), oos])`) - do not
  hardcode 500 for short trains.
- **Do not re-break the fixed bugs:** (1) ATR for entry/stop is taken as
  `atr[i-1]` - known at decision time; (2) pooled/per-name equity aggregation is
  ONLY via `_daily_pnl()` (groupby-SUM by exit date). A dict comprehension keyed
  on exit date silently overwrites same-day trades and understates the pool
  (was a bug: 4.0->5.2M instead of 4.0->12.0M); (3) pooled and per-name equity
  are **mark-to-market** via `pooled_equity()` - summing realised PnL alone skips
  the entire life of every open position and understated the pool drawdown by
  roughly a third (-14.1% instead of -18.1% on an 8-name sample); (4) the OOS
  span is `folds * oos_days / 252`, not `(n - train - oos)/252`, which dropped a
  whole fold and inflated %/yr by 4.5% relative.

## 4. Data

- `data/candles_ru_daily/*.csv` - 43 CSVs of MOEX **daily** bars (interval 24),
  columns `timestamp,open,high,low,close,volume`, midnight-stamped,
  2015/2017 -> 2026-08 (~2,818 bars per name). Exceptions: POSI (IPO 2021-12),
  POLY (delisted 2024), SMLT/VKCO (IPO 2020/2021).
- The universe `ORIG U NEW` is defined in `universe.py`
  (ORIG = 24 stocks + IMOEX; NEW = 15 added). **Keep the ENRU/RSTI/YDEX CSVs
  OUT of any basket runs** (delistings / short history - truncation bias). The
  package scripts are already pinned to ORIG U NEW rather than globbing.
- IMOEX is in the universe only as an index reference name; tradability is
  realized through the IMOEXF stock future (it is included in the pool
  allocation).

## 5. Building the report

```powershell
python report_build.py   # from the root of this folder
```

- Depends on `pandas/numpy/matplotlib` (see `requirements.txt`);
  `matplotlib.use("Agg")` is already set in the scripts.
- Walk-forward runs are cached in `report/tables/_cache/*.pkl` under a key that
  covers the config, the parameter grid, the walk-forward windows, the universe
  AND a content hash of the source CSVs. Refreshing the bars or changing a
  parameter therefore invalidates the cache instead of silently returning the
  previous run (it did not, before 2026-09-12: "download fresh bars, then
  rebuild" produced the old report). `python report_build.py --no-cache` forces a
  full recompute.
- Output: `report/images/*.png` (16: 8 general + 8 signal),
  `report/tables/*.csv` (8), HTML + MD.
- **Signal charts** (`chart_signals`): candles, full history (from the first bar,
  2015-06-09+ -> 2026-08, PNG 16,330 x 1,104 px - width is for horizontal
  scrolling in the HTML). Colors: green candle = up day, red = down day
  (`#26a69a`/`#e74c3c`); markers - **blue triangle = BUY** (open long / close
  short), **orange inverted = SELL** (open short / close long), s=90, white
  edge; exits carry a PnL label (blue = profit, orange = loss). Bottom panel -
  mark-to-market sub-account equity
  `POOL + cumsum(realized) + unrealized(open positions revalued at close)`.
- **HTML document** `report/report.html` - a self-contained twin of
  `report.md`. When editing, keep the `<div class="scroll">`
  (overflow-x:auto) wrappers around the 8 signal `<img>` and `class="normal"` on
  the rest - VS Code's MD preview caps image width and cannot scroll
  horizontally.
- Language: report artifacts are in Russian, console output in English
  (convention); Cyrillic in charts via DejaVu Sans.

## 6. Integration checks (after any change)

```powershell
# builder + modules import cleanly
python -c "import report_build, strategy_research, universe"

# data reads
python -c "import pandas as pd; d=pd.read_csv('data/candles_ru_daily/SBER.csv'); print(len(d), d['close'].iloc[-1])"

# HTML image refs resolve (run from this folder)
python -c "import re,os; h=open('report/report.html',encoding='utf-8').read(); miss=[r for r in set(re.findall(r'src=\"(images/[^\"]+)\"',h)) if not os.path.exists('report/'+r)]; print('missing:', miss or 'NONE')"

# 40-name universe
python -c "from universe import ORIG, NEW; print(len(set(ORIG)|set(NEW)))"
```

## 7. Disclaimer (do not remove - the legal frame of the package)

Not modeled: quarterly futures roll (carry is in the futures price), stop
gap-through on limit-down days, liquidity at 10-15% notional in a mid-cap.
Shorts in the backtest go through futures (no overnight borrow fee); roll gaps
and short-position risks are not captured in practice. All of this is a
theoretical ceiling, NOT a trading recommendation.