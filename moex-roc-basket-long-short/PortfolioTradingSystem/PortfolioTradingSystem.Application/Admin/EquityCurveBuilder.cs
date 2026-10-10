using PortfolioTradingSystem.Application.Engine;
using PortfolioTradingSystem.Application.Ports;
using PortfolioTradingSystem.Domain.Models;
using PortfolioTradingSystem.Domain.Strategy;

namespace PortfolioTradingSystem.Application.Admin;

/// <summary>
/// Builds the daily equity curve for a set of instruments (the whole portfolio or
/// a single instrument for the details page). Extracted from the admin HTTP
/// endpoint so it is unit-testable against in-memory fakes. Dates are Moscow
/// dates throughout - bar times arrive with the Moscow offset while trade times
/// come back from Postgres in UTC, so .DateTime.Date would silently mix two
/// calendars; every day comparison goes through MoscowClock.ToMoscowDate.
/// </summary>
public static class EquityCurveBuilder
{
    public static Task<EquityDataDto> BuildAsync(
        IReadOnlyList<Instrument> instruments,
        IEngineSupervisor supervisor,
        IHistoricDailyBarsProvider history,
        ITradeLogRepository tradeLogs,
        IPositionRepository positions,
        IMetricsStore metrics,
        StrategyOptions options,
        CancellationToken ct)
        => BuildAsync(instruments, supervisor, history, tradeLogs, positions, metrics,
            options, DateTimeOffset.Now, ct);

    public static async Task<EquityDataDto> BuildAsync(
        IReadOnlyList<Instrument> instruments,
        IEngineSupervisor supervisor,
        IHistoricDailyBarsProvider history,
        ITradeLogRepository tradeLogs,
        IPositionRepository positions,
        IMetricsStore metrics,
        StrategyOptions options,
        DateTimeOffset now,
        CancellationToken ct)
    {
        decimal pool = instruments.Count * options.InitialCapital;
        decimal commission = options.CommissionPct / 100m;
        var allTrades = await tradeLogs.GetAllAsync(ct).ConfigureAwait(false);

        var openByTicker = new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase);
        foreach (var inst in instruments)
        {
            var open = await positions.GetOpenAsync(inst.Id, ct).ConfigureAwait(false);
            if (open is not null)
            {
                openByTicker[inst.Ticker] = open;
            }
        }

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

        foreach (var inst in instruments)
        {
            IReadOnlyList<Candle> bars;
            var engine = supervisor.GetEngine(inst.Id);
            if (engine is not null)
            {
                var list = engine.GetDailyBars().ToList();
                if (engine.CurrentSessionBar is { } live && (list.Count == 0 || list[^1].Time < live.Time))
                {
                    list.Add(live);
                }

                bars = list;
            }
            else if (InstrumentEngine.EffectiveInstrumentId(inst).Length > 0)
            {
                try
                {
                    bars = await history.GetDailyBarsAsync(
                        InstrumentEngine.EffectiveInstrumentId(inst), 700, ct).ConfigureAwait(false);
                }
                catch
                {
                    bars = Array.Empty<Candle>();
                }
            }
            else
            {
                bars = Array.Empty<Candle>();
            }

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

            closeByDateByTicker[inst.Ticker] = byDate;
        }

        foreach (var trade in allTrades)
        {
            AddDay(trade.EntryTime);
            AddDay(trade.ExitTime);
        }

        foreach (var open in openByTicker.Values)
        {
            AddDay(open.EntryTime);
        }

        static decimal MarkToMarket(decimal close, int dir, decimal entryPrice, int units,
                                    decimal pointRub, decimal commissionRate)
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

        foreach (var trade in allTrades)
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
                    unrealized[i] += MarkToMarket(
                        close, (int)trade.Direction, trade.EntryPrice, trade.Units,
                        options.PointRub, commission);
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
                    unrealized[i] += MarkToMarket(
                        close, (int)open.Direction, open.EntryPrice, open.Units,
                        options.PointRub, commission);
                }
            }
        }

        if (startDay is null && days.Count > 0)
        {
            startDay = days[^1];
        }

        var points = new List<EquityPointDto>(days.Count + 1);
        decimal realizedRunning = 0m;
        for (int i = 0; i < days.Count; i++)
        {
            realizedRunning += realized[i];
            if (startDay is { } from && days[i] < from)
            {
                continue;
            }

            points.Add(new EquityPointDto(timeByDay[days[i]], pool + realizedRunning + unrealized[i]));
        }

        decimal totalRealized = allTrades.Sum(t => t.PnlRub);
        decimal unrl = instruments.Sum(i => metrics.Get(i.Id)?.PositionPnl ?? 0m);
        if (points.Count == 0)
        {
            points.Add(new EquityPointDto(now, pool + unrl));
        }
        else if (points[^1].Time < now)
        {
            points.Add(new EquityPointDto(now, pool + totalRealized + unrl));
        }

        return new EquityDataDto(points, pool, totalRealized, unrl, pool + totalRealized + unrl);
    }
}