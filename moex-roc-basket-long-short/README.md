# moex-roc-basket-long-short

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

A daily **ROC-momentum basket** of 40 liquid MOEX stocks, **long + short, with
no leverage** (`leverage = 1`, per-trade notional <= cash). This is the validated
(walk-forward OOS) configuration of the `roc_momentum` algorithm on daily bars —
the same one previously run at 7x leverage and long-only, now without margin
financing and shorting via stock futures on the underlying.

> **Headline result (OOS, 4.0M RUB pool, 9.1 years):** **+8,043,568 RUB = +201.1% =
> +22.1%/yr**, pool maxDD -9.3%, 7,192 trades, win 50.2%, avg +1,118 RUB/trade,
> PF 1.40, **40/40 names and 9/10 years positive**.

---

## Idea

- **Signal.** At the close of day *i*, `ROC_n = close / close[i-n] - 1` (n = 5).
  If `ROC > 0.01` -> long; if `ROC < -0.01` -> short.
- **Entry.** On the next bar's open. At most one open position per name, and a
  bar that produced an exit cannot also produce an entry - the position was still
  open at that bar's open.
- **Exit.** Stop 2*ATR or take-profit 3*ATR (ATR known at decision time - no
  look-ahead). A bar opening beyond the level fills at the open. Trailing stop off.
- **Position sizing.** `units = risk% * equity / (2*ATR)`, capped so
  `notional <= cash` (leverage 1). At risk 10% positions saturate to "all-in".
- **No leverage.** Shorts are executed via stock futures - no overnight margin
  / borrow financing, so the whole package is executable in practice.

## Configuration

| Parameter | Value |
|---|---|
| Algorithm | `roc_momentum`, n=5, threshold=0.01 |
| Bar | MOEX daily (interval 24), 2015/2017 -> 2026-08 |
| Universe | 40 names = 25 original + 15 added (see `universe.py`) |
| Sides | long + short |
| Leverage | 1 (no borrowed money) |
| Stops | SL 2*ATR / TP 3*ATR, trail 0 |
| Commission | 0.04% of turnover (T-Bank "Trader" tariff, futures-style) |
| Walk-forward | train 504 bars / OOS 126 bars, min_trades 3 |
| Risk (test) | 10% (risk table in the report) |
| Pool | 4.0M RUB (100k per name) |

## Results (walk-forward OOS)

- **+8,043,568 RUB = +201.1% over 9.1 years = +22.1%/yr**, pool maxDD **-9.3%**,
  7,192 trades, win 50.2%, avg +1,118 RUB, PF 1.40.
- **Long/short split:** shorts 3,379 trades (47%) = +4,291,635 RUB (53% of
  profit), PF 1.41; longs 3,813 trades = +3,751,932 RUB, PF 1.39. Both sides
  independently profitable.
- **Coverage:** 40/40 names positive, 9/10 years positive (only 2018 = -3.4%,
  a "bear" year where shorts partly offset longs).
- **Risk scan (leverage 1):** 1% -> +4.0%/-1.4%; 5% -> +18.2%/-5.3%;
  10% -> +22.1%/-9.3%; 15% -> +22.0%/-12.5% (saturates ~10%: beyond that positions
  are already all-in, only the drawdown grows).
- **Reference only (NOT a recommendation):** same with 2x leverage ->
  +39.3%/yr/-9.0%; 3x -> +46.2%/-11.6%; 5-10x -> ~+47.5%/-13%. Leverage is
  **forbidden** here: margin financing is not modeled.

## Contents

```
report/report.html  - report (main document; open in browser, wide
                                signal charts scroll horizontally)
report/report.md    - same report in Markdown (renders on GitHub)
report/images/                - 16 charts (8 general + 8 signal)
report/tables/                - report CSVs + walk-forward run cache (_cache/)
|
report_build.py               - report builder (rebuild everything)
strategy_research.py          - backtest framework (walk-forward, ROC-momentum,
                                simulator with SL/TP and risk sizing)
universe.py                 - shared 40-name universe (ORIG = 25 + NEW = 15) and
                                IMOEX per-year returns helper
moex_data_downloader.py       - MOEX daily-bar downloader (ISS API)
data/candles_ru_daily/        - source daily bars (43 CSVs, 2015/2017 -> 2026-08)
```

## How to reproduce

```powershell
# 1. dependencies
pip install -r requirements.txt

# 2. (optional) fetch fresh daily bars into data/candles_ru_daily/
python moex_data_downloader.py --stocks AFLT,ALRS,...,VTBR --interval 24 --days 4250

# 3. rebuild the report; the cache key covers the config, the grid, the windows
#    and a hash of the CSVs, so fresh bars force a recompute on their own
python report_build.py          # --no-cache to ignore the cache entirely
```

The build scripts expect data in
`data/candles_ru_daily/` - relative paths are already set, nothing to configure.

## Disclaimer

The backtest does **not** model: quarterly futures roll (carry is in the futures
price), **dividend gaps** (daily MOEX bars are unadjusted, so a long held through
an ex-date eats a gap it would have been paid and a short books a gain it would
have to pay out - or that does not exist at all when the leg is a future), or
liquidity at 10-15% notional in a mid-cap. Stop gap-through *is* modelled now
(a bar opening beyond the level fills at the open); slippage and exchange lots
are available (`--slippage`, `--lot`) but default to off. The numbers are a theoretical ceiling of the algorithm on historical
data, **not a trading recommendation**. Past performance does not guarantee
future returns.