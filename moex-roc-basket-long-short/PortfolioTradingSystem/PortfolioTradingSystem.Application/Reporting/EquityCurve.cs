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
        // re-summed every trade twice. It is now one pass over the trades (plus
        // their holding days) and one pass over the days per ticker. Dates are
        // Moscow dates throughout - bar times
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

        // Last close on or before each day, per ticker. A day without a bar for a
        // ticker (a thin name, a paused instrument whose history ends earlier)
        // keeps its position at the last known price instead of dropping it from
        // that day's equity - which drew a spurious one-day dip.
        var closeAtByTicker = new Dictionary<string, decimal?[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ticker, byDate) in closeByDateByTicker)
        {
            var closeAt = new decimal?[days.Count];
            decimal? last = null;
            for (int i = 0; i < days.Count; i++)
            {
                if (byDate.TryGetValue(days[i], out decimal close))
                {
                    last = close;
                }

                closeAt[i] = last;
            }

            closeAtByTicker[ticker] = closeAt;
        }

        // realized[i] = PnL of the trades that closed ON days[i]. A trade is marked
        // to the close on the days it was held overnight and counted at its exit
        // price on its exit day. It used to be marked to the exit day's close and
        // realized only the next day, which misstated every exit day - and every
        // signal_reversal flip day, whose old leg exits at the open - by
        // (close - exit) x units.
        var realized = new decimal[days.Count];
        var unrealized = new decimal[days.Count];
        DateOnly? startDay = null;

        foreach (var trade in trades)
        {
            var entryDay = trade.EntryTime.ToMoscowDate();
            var exitDay = trade.ExitTime.ToMoscowDate();
            startDay = startDay is null || entryDay < startDay ? entryDay : startDay;

            // Both days were added to the calendar above.
            int exitIdx = dayIndex[exitDay];
            realized[exitIdx] += trade.PnlRub;

            if (!closeAtByTicker.TryGetValue(trade.Ticker, out var closeAt))
            {
                continue;
            }

            for (int i = dayIndex[entryDay]; i < exitIdx; i++)
            {
                if (closeAt[i] is { } close)
                {
                    unrealized[i] += MarkToMarket(close, (int)trade.Direction, trade.EntryPrice, trade.Units);
                }
            }
        }

        foreach (var (ticker, open) in openByTicker)
        {
            var entryDay = open.EntryTime.ToMoscowDate();
            startDay = startDay is null || entryDay < startDay ? entryDay : startDay;
            if (!closeAtByTicker.TryGetValue(ticker, out var closeAt))
            {
                continue;
            }

            for (int i = dayIndex[entryDay]; i < days.Count; i++)
            {
                if (closeAt[i] is { } close)
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
