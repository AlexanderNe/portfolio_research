"""
Download MOEX candles (official exchange feed, no auth) via ISS API.

Endpoints:
  stocks : https://iss.moex.com/iss/engines/stock/markets/shares/boards/TQBR/securities/{SEC}/candles.json
  index  : https://iss.moex.com/iss/engines/stock/markets/index/securities/{SEC}/candles.json
  futures: https://iss.moex.com/iss/engines/futures/markets/forts/securities/{SEC}/candles.json

Intervals: 1 (min), 10 (min), 60 (min), 24 (daily).

Output CSV (same schema as strategy_research.py):
  timestamp (naive Moscow time), open, high, low, close, volume

A download MERGES into the existing file (new bars win on a timestamp clash),
so a short --days window tops the history up instead of truncating it.
"""

import sys
import time
from datetime import date, timedelta
from pathlib import Path

import pandas as pd
import requests

RETRIES = 4
PAGE_PAUSE = 0.15

STOCKS = [
    "SBER", "SBERP", "GAZP", "LKOH", "ROSN", "NVTK", "GMKN", "TATN",
    "MGNT", "VTBR", "PLZL", "POLY", "ALRS", "CHMF", "NLMK", "MTSS",
    "SNGSP", "AFLT", "POSI", "RUAL", "IRAO", "FEES", "TRNFP", "CBOM",
]

INDEXES = ["IMOEX"]

CANDLE_URL = (
    "https://iss.moex.com/iss/engines/{engine}/markets/{market}/"
    "{board}securities/{sec}/candles.json"
)


def _get_json(url: str, params: dict) -> dict:
    """One ISS request with retries. Raises if it cannot be completed.

    TLS verification stays ON: these candles are the input to trading
    decisions, so a silently intercepted feed is not an acceptable trade-off.
    """
    headers = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}
    last = None
    for attempt in range(RETRIES):
        try:
            r = requests.get(url, params=params, headers=headers, timeout=60)
            r.raise_for_status()
            return r.json()
        except requests.exceptions.RequestException as e:
            last = e
            if attempt < RETRIES - 1:
                time.sleep(2 ** attempt)
    raise RuntimeError(f"ISS request failed after {RETRIES} attempts: {last}")


def fetch_candles(engine: str, market: str, board: str, sec: str, interval: int,
                  start: date, till: date):
    """Fetch all candle pages in [start, till]; returns (rows, columns).

    For fine intervals (1/10 min) the range is chunked into ~35-day slices so
    the per-page total stays well under the cap and failures are recoverable.
    A chunk that cannot be completed raises instead of silently leaving a hole
    in the history.
    """
    rows = []
    columns = None
    chunk_days = 35 if interval <= 10 else 365
    c0 = start
    url = CANDLE_URL.format(engine=engine, market=market, sec=sec,
                            board=("boards/%s/" % board) if board else "")
    while c0 < till:
        c1 = min(c0 + timedelta(days=chunk_days), till)
        page_start = 0
        pages = 0
        base = len(rows)
        while True:
            pages += 1
            if pages > 1000:
                raise RuntimeError(f"{sec}: more than 1000 pages for {c0}..{c1}")
            j = _get_json(url, {
                "interval": interval,
                "from": c0.isoformat(),
                "till": c1.isoformat(),
                "start": page_start,
            })
            candles = j.get("candles") or {}
            if columns is None:
                columns = candles.get("columns")
            data = candles.get("data") or []
            if not data:
                break
            rows.extend(data)
            page_start += len(data)
            time.sleep(PAGE_PAUSE)
        print(f"    {sec} {interval}m: chunk {c0}..{c1} done, "
              f"{len(rows) - base} rows ({len(rows)} total, {(c1 - start).days}/{ (till - start).days } days)",
              flush=True)
        c0 = c1
    return rows, columns


def candles_to_df(rows: list, columns) -> pd.DataFrame:
    """Build the OHLCV frame using the column names ISS reported.

    Reading columns positionally (the previous behaviour) silently swaps OHLC
    if the endpoint ever reorders them.
    """
    if not rows:
        return pd.DataFrame()
    if not columns:
        raise ValueError("ISS response carried no column names")
    df = pd.DataFrame(rows, columns=columns)
    required = {"open", "high", "low", "close", "volume", "begin"}
    missing = required - set(df.columns)
    if missing:
        raise ValueError(f"ISS candles are missing columns: {sorted(missing)}")
    if df.empty:
        return df
    df = df[["open", "high", "low", "close", "volume", "begin"]].rename(
        columns={"begin": "timestamp"})
    df["timestamp"] = pd.to_datetime(df["timestamp"])
    df["volume"] = df["volume"].fillna(0).astype("int64")
    for c in ("open", "high", "low", "close"):
        df[c] = df[c].astype(float)
    df = df.drop_duplicates(subset="timestamp").sort_values("timestamp").reset_index(drop=True)
    return df[["timestamp", "open", "high", "low", "close", "volume"]]


def fetch_one(engine: str, market: str, board: str, sec: str, interval: int,
              start: date, till: date):
    rows, columns = fetch_candles(engine, market, board, sec, interval, start, till)
    df = candles_to_df(rows, columns)
    if not df.empty:
        df = df[(df["timestamp"].dt.date >= start) & (df["timestamp"].dt.date <= till)]
    return df


def merge_history(path: Path, fresh: pd.DataFrame) -> pd.DataFrame:
    """Merge a freshly downloaded window into the file already on disk.

    The downloader used to overwrite the CSV with only what the request
    returned, so `--days 365` silently destroyed years of accumulated history.
    """
    if path.exists():
        old = pd.read_csv(path)
        old["timestamp"] = pd.to_datetime(old["timestamp"])
        fresh = pd.concat([old, fresh], ignore_index=True)
    return (fresh.drop_duplicates(subset="timestamp", keep="last")
                 .sort_values("timestamp").reset_index(drop=True))


INTERVALS = {
    1: "candles_ru_1min",
    10: "candles_ru_10min",
    60: "candles_ru_1h",
    24: "candles_ru_daily",
}


def filter_session(df: pd.DataFrame, interval: int) -> pd.DataFrame:
    """Keep only regular weekday main-session bars (Mon-Fri, 10:00-18:50 Moscow).

    Drops weekend/evening trading sessions and leaves bars aligned to the
    main session so intraday strategies never trade those sessions.
    """
    end_ok = (df["timestamp"].dt.hour < 18) | (
        (df["timestamp"].dt.hour == 18) & (df["timestamp"].dt.minute <= 50)
    )
    df = df[(df["timestamp"].dt.weekday < 5)
            & (df["timestamp"].dt.hour >= 10)
            & end_ok]
    return df.reset_index(drop=True)


def main():
    import argparse
    ap = argparse.ArgumentParser(description="Download MOEX candle history via ISS")
    ap.add_argument("--days", type=int, default=365,
                    help="lookback window in days (default 365)")
    ap.add_argument("--stocks", type=str, default=None,
                    help="comma-separated subset of tickers (default: all)")
    ap.add_argument("--interval", type=int, choices=[1, 10, 60, 24], default=None,
                    help="bar interval in minutes / 24 for daily (default: 60+24); "
                         "use 1 for 1-min candles")
    args = ap.parse_args()

    days = args.days
    till = date.today()
    start = till - timedelta(days=days)
    print(f"fetching {days} days ({start}..{till}) from MOEX ISS")

    stocks = STOCKS
    if args.stocks:
        stocks = [s.upper() for s in args.stocks.split(",") if s.strip()]

    targets = [(s, "stock", "shares", "TQBR") for s in stocks]
    if not args.stocks or "IMOEX" in {s.upper() for s in args.stocks.split(",")}:
        targets += [("IMOEX", "stock", "index", "")]

    intervals = [args.interval] if args.interval else [60, 24]

    for sec, engine, market, board in targets:
        for interval in intervals:
            out_dir = Path("data") / INTERVALS[interval]
            out_dir.mkdir(parents=True, exist_ok=True)
            tag = f"{interval}m" if interval < 60 else ("1h" if interval == 60 else "daily")
            try:
                df = fetch_one(engine, market, board, sec, interval, start, till)
                if df.empty:
                    print(f"[{sec}] no {tag} data")
                    continue
                if interval == 24:
                    df = df[df["timestamp"].dt.weekday < 5].reset_index(drop=True)
                else:
                    df = filter_session(df, interval)
                if df.empty:
                    print(f"[{sec}] no {tag} data after session filter")
                    continue
                path = out_dir / f"{sec}.csv"
                before = len(pd.read_csv(path)) if path.exists() else 0
                df = merge_history(path, df)
                df.to_csv(path, index=False)
                print(f"[{sec}] saved {len(df)} {tag} bars "
                      f"(+{len(df) - before} new, "
                      f"{df['timestamp'].min()}..{df['timestamp'].max()}, "
                      f"{df['timestamp'].dt.date.nunique()} days)")
            except Exception as e:
                print(f"[{sec}] ERROR ({tag}): {e}")
            time.sleep(0.3)


if __name__ == "__main__":
    main()