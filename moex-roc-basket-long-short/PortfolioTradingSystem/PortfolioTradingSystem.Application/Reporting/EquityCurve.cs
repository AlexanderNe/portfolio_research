using PortfolioTradingSystem.Application.Engine;
using PortfolioTradingSystem.Domain.Models;

namespace PortfolioTradingSystem.Application.Reporting;

/// <summary>One end-of-day point of the pooled equity curve.</summary>
public readonly record struct EquityPoint(DateTimeOffset Time, decimal Equity);

/// <summary>
/// Pooled end-of-day equity: initial pool + realized PnL + positions marked to the
/// day's close. Pure: the admin endpoint gathers the inputs, this only computes.
/// </summary>
public static class EquityCurve
{
    /// <param name="pool">Initial pool (instrument count x initial capital).</param>
    /// <param name="trades">All closed trades.</param>
    /// <param name="openByTicker">Currently open position per ticker.</param>
    /// <param name="barsByTicker">Daily bars per ticker, ascending.</param>
    /// <param name="pointRub">RUB value of one price point per unit.</param>
    /// <param name="commissionRate">Commission per side as a fraction (0.0004 = 0.04%).</param>
    public static IReadOnlyList<EquityPoint> Build(
        decimal pool,
        IReadOnlyList<TradeLogEntry> trades,
        IReadOnlyDictionary<string, OpenPosition> openByTicker,
        IReadOnlyDictionary<string, IReadOnlyList<Candle>> barsByTicker,
        decimal pointRub,
        decimal commissionRate)
    {
        // Rebuilt from an O(days x trades) double loop: for every day it
        // re-summed every trade twice. It is now one pass over the trades plus
        // one pass over the days. Dates are Moscow dates throughout - bar times
        // arrive with the Moscow offset while trade times come back from
        // Postgres in UTC, so .DateTime.Date silently mixed two calendars.
        var closeByDateByTicker = new Dictionary<string, Dictionary<DateOnly, decimal>>(StringComparer.OrdinalIgnoreCase);
        var timeByDay = new Dictionary<DateOnly, DateTimeOffset>();
        var daySet = new SortedSet<DateOnly>();

        void AddDay(DateTimeOffset time)
        {
            var date = time.ToMoscowDate();
            daySet.Add(date);
            if (!timeByDay.ContainsKey(date))
            {
                timeByDay[date] = time;
            }
        }

        foreach (var (ticker, bars) in barsByTicker)
        {
            var byDate = new Dictionary<DateOnly, decimal>();
            foreach (var c in bars)
            {
                AddDay(c.Time);
                byDate[c.Time.ToMoscowDate()] = c.Close;
            }

            if (byDate.Count == 0)
            {
                continue;
            }

            closeByDateByTicker[ticker] = byDate;
        }

        foreach (var trade in trades)
        {
            AddDay(trade.EntryTime);
            AddDay(trade.ExitTime);
        }

        foreach (var open in openByTicker.Values)
        {
            AddDay(open.EntryTime);
        }

        decimal MarkToMarket(decimal close, int dir, decimal entryPrice, int units)
        {
            decimal gross = (close - entryPrice) * units * dir * pointRub;
            decimal closeCommission = close * units * commissionRate;
            decimal openCommission = entryPrice * units * commissionRate;
            return gross - closeCommission - openCommission;
        }

        var days = daySet.ToList();
        var dayIndex = new Dictionary<DateOnly, int>(days.Count);
        for (int i = 0; i < days.Count; i++)
        {
            dayIndex[days[i]] = i;
        }

        // realized[i] = PnL of trades that closed strictly BEFORE days[i];
        // a trade is marked to market on its exit day, realized from the next.
        var realized = new decimal[days.Count + 1];
        var unrealized = new decimal[days.Count + 1];
        DateOnly? startDay = null;

        foreach (var trade in trades)
        {
            var entryDay = trade.EntryTime.ToMoscowDate();
            var exitDay = trade.ExitTime.ToMoscowDate();
            startDay = startDay is null || entryDay < startDay ? entryDay : startDay;
            if (dayIndex.TryGetValue(exitDay, out int exitIdx) && exitIdx + 1 < realized.Length)
            {
                realized[exitIdx + 1] += trade.PnlRub;
            }

            if (!closeByDateByTicker.TryGetValue(trade.Ticker, out var closes)
                || !dayIndex.TryGetValue(entryDay, out int from))
            {
                continue;
            }

            int to = dayIndex.TryGetValue(exitDay, out int idx) ? idx : days.Count - 1;
            for (int i = from; i <= to && i < days.Count; i++)
            {
                if (closes.TryGetValue(days[i], out decimal close))
                {
                    unrealized[i] += MarkToMarket(close, (int)trade.Direction, trade.EntryPrice, trade.Units);
                }
            }
        }

        foreach (var (ticker, open) in openByTicker)
        {
            var entryDay = open.EntryTime.ToMoscowDate();
            startDay = startDay is null || entryDay < startDay ? entryDay : startDay;
            if (!closeByDateByTicker.TryGetValue(ticker, out var closes)
                || !dayIndex.TryGetValue(entryDay, out int from))
            {
                continue;
            }

            for (int i = from; i < days.Count; i++)
            {
                if (closes.TryGetValue(days[i], out decimal close))
                {
                    unrealized[i] += MarkToMarket(close, (int)open.Direction, open.EntryPrice, open.Units);
                }
            }
        }

        if (startDay is null && days.Count > 0)
        {
            startDay = days[^1];
        }

        var points = new List<EquityPoint>(days.Count + 1);
        decimal realizedRunning = 0m;
        for (int i = 0; i < days.Count; i++)
        {
            realizedRunning += realized[i];
            if (startDay is { } first && days[i] < first)
            {
                continue;
            }

            points.Add(new EquityPoint(timeByDay[days[i]], pool + realizedRunning + unrealized[i]));
        }

        return points;
    }
}
