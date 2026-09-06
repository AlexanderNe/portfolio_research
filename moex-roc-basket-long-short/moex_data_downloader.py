"""
Download MOEX candles (official exchange feed, no auth) via ISS API.

Endpoints:
  stocks : https://iss.moex.com/iss/engines/stock/markets/shares/boards/TQBR/securities/{SEC}/candles.json
  index  : https://iss.moex.com/iss/engines/stock/markets/index/securities/{SEC}/candles.json
  futures: https://iss.moex.com/iss/engines/futures/markets/forts/securities/{SEC}/candles.json

Intervals: 1 (min), 10 (min), 60 (min), 24 (daily).

Output CSV (same schema as strategy_research.py):
  timestamp (naive Moscow time), open, high, low, close, volume
"""

import sys
import time
import warnings
from datetime import date, timedelta
from pathlib import Path

import pandas as pd
import requests
from requests.packages.urllib3.exceptions import InsecureRequestWarning

warnings.filterwarnings("ignore", category=InsecureRequestWarning)

STOCKS = [
    "SBER", "SBERP", "GAZP", "LKOH", "ROSN", "NVTK", "GMKN", "TATN",
    "MGNT", "VTBR", "PLZL", "POLY", "ALRS", "CHMF", "NLMK", "MTSS",
    "SNGSP", "AFLT", "POSI", "RUAL", "IRAO", "FEES", "TRNFP", "CBOM",
]

INDEXES = ["IMOEX"]

CANDLE_URL = (
    "https://iss.moex.com/iss/engines/{engine}/markets/{market}/"
    "securities/{sec}/candles.json"
)


def fetch_candles(engine: str, market: str, sec: str, interval: int,
                  start: date, till: date) -> list:
    """Fetch all candle pages in [start, till] for the given interval.

    For fine intervals (1/10 min) the range is chunked into ~35-day slices so
    the per-page total stays well under the cap and failures are recoverable.
    """
    rows = []
    chunk_days = 35 if interval <= 10 else 365
    c0 = start
    while c0 < till:
        c1 = min(c0 + timedelta(days=chunk_days), till)
        page_start = 0
        pages = 0
        headers = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}
        url = CANDLE_URL.format(engine=engine, market=market, sec=sec)
        base = len(rows)
        while True:
            pages += 1
            if pages > 1000:
                print("    too many pages, stopping")
                break
            params = {
                "interval": interval,
                "from": c0.isoformat(),
                "till": c1.isoformat(),
                "start": page_start,
            }
            try:
                r = requests.get(url, params=params, headers=headers,
                                 timeout=60, verify=False)
                r.raise_for_status()
                j = r.json()
            except requests.exceptions.RequestException as e:
                print(f"    request error: {e}")
                time.sleep(3)
                break
            data = ((j.get("candles") or {}).get("data") or [])
            rows.extend(data)
            page_start += len(data)
            if len(data) < 500 or not data:
                break
            time.sleep(0.15)
        if pages >= 1000:
            break
        print(f"    {sec} {interval}m: chunk {c0}..{c1} done, "
              f"{len(rows) - base} rows ({len(rows)} total, {(c1 - start).days}/{ (till - start).days } days)",
              flush=True)
        c0 = c1
    return rows


def candles_to_df(rows: list) -> pd.DataFrame:
    df = pd.DataFrame(rows, columns=["open", "close", "high", "low",
                                     "value", "volume", "begin", "end"])
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


def fetch_one(engine: str, market: str, sec: str, interval: int,
              start: date, till: date):
    rows = fetch_candles(engine, market, sec, interval, start, till)
    df = candles_to_df(rows)
    if not df.empty:
        df = df[(df["timestamp"].dt.date >= start) & (df["timestamp"].dt.date <= till)]
    return df


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
                df = fetch_one(engine, market, sec, interval, start, till)
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
                df.to_csv(path, index=False)
                print(f"[{sec}] saved {len(df)} {tag} bars "
                      f"({df['timestamp'].min()}..{df['timestamp'].max()}, "
                      f"{df['timestamp'].dt.date.nunique()} days)")
            except Exception as e:
                print(f"[{sec}] ERROR ({tag}): {e}")
            time.sleep(0.3)


if __name__ == "__main__":
    main()