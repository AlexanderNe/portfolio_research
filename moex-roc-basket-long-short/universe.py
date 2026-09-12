"""Shared universe definitions and helpers for the MOEX ROC-momentum basket.

Used by `report_build.py` to build the long+short (no-leverage) report.
Kept separate from the report builder so the universe belongs to the strategy,
not to any one report variant.
"""

from pathlib import Path
import pandas as pd

DATA = str(Path(__file__).resolve().parent / "data" / "candles_ru_daily")

NEW = ["MOEX", "SIBN", "MAGN", "PIKK", "SMLT", "MRKC", "VKCO", "HYDR",
       "TGKA", "SNGS", "OGKB", "KMAZ", "MVID", "RTKMP", "BSPB"]

ORIG = ["AFLT", "ALRS", "CBOM", "CHMF", "FEES", "GAZP", "GMKN", "IMOEX",
        "IRAO", "LKOH", "MGNT", "MTSS", "NLMK", "NVTK", "PLZL", "POLY",
        "POSI", "ROSN", "RUAL", "SBER", "SBERP", "SNGSP", "TATN", "TRNFP",
        "VTBR"]

BASKET40 = sorted(set(ORIG) | set(NEW))


def moex_year_returns(start=None):
    """IMOEX price return per calendar year from `start` onwards.

    `start` should be the first day the strategy is actually measured on, so the
    benchmark covers the same window; it used to be a hardcoded date unrelated to
    the walk-forward schedule.
    """
    df = pd.read_csv("%s/IMOEX.csv" % DATA)
    df["timestamp"] = pd.to_datetime(df["timestamp"])
    df = df.sort_values("timestamp").reset_index(drop=True)
    start = pd.Timestamp(start) if start is not None else df["timestamp"].iloc[0]
    df = df[df["timestamp"] >= start].reset_index(drop=True)
    if df.empty:
        return {}
    out = {}
    for y in sorted(set(df["timestamp"].dt.year)):
        sub = df[df["timestamp"].dt.year == y]
        before = df[df["timestamp"] < sub["timestamp"].iloc[0]]
        base = before["close"].iloc[-1] if len(before) else \
            sub["close"].iloc[0]
        out[y] = (sub["close"].iloc[-1] / base - 1) * 100
    return out
