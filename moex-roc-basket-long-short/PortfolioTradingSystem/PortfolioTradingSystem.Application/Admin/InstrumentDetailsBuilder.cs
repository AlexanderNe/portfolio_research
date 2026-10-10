using PortfolioTradingSystem.Application.Engine;
using PortfolioTradingSystem.Application.Ports;
using PortfolioTradingSystem.Domain.Enums;
using PortfolioTradingSystem.Domain.Models;
using PortfolioTradingSystem.Domain.Strategy;

namespace PortfolioTradingSystem.Application.Admin;

/// <summary>
/// Builds the per-instrument details payload (price chart plus realized /
/// unrealized PnL, pending signal and recent signals). Extracted from the admin
/// HTTP endpoint so the computation is unit-testable against in-memory fakes.
/// </summary>
public static class InstrumentDetailsBuilder
{
    /// <summary>Number of most recent signal-log entries included in the payload.</summary>
    public const int RecentSignalsLimit = 50;

    public static Task<InstrumentDetailsDto> BuildAsync(
        Instrument instrument,
        IEngineSupervisor supervisor,
        IHistoricDailyBarsProvider history,
        ITradeLogRepository tradeLogs,
        IPositionRepository positions,
        IMetricsStore metrics,
        ISignalLogRepository signals,
        StrategyOptions options,
        CancellationToken ct)
        => BuildAsync(instrument, supervisor, history, tradeLogs, positions, metrics, signals,
            options, DateTimeOffset.UtcNow, ct);

    public static async Task<InstrumentDetailsDto> BuildAsync(
        Instrument instrument,
        IEngineSupervisor supervisor,
        IHistoricDailyBarsProvider history,
        ITradeLogRepository tradeLogs,
        IPositionRepository positions,
        IMetricsStore metrics,
        ISignalLogRepository signals,
        StrategyOptions options,
        DateTimeOffset now,
        CancellationToken ct)
    {
        IReadOnlyList<Candle> candles;
        var engine = supervisor.GetEngine(instrument.Id);
        if (engine is not null)
        {
            var bars = engine.GetDailyBars().ToList();
            if (engine.CurrentSessionBar is { } live && (bars.Count == 0 || bars[^1].Time < live.Time))
            {
                bars.Add(live);
            }

            candles = bars;
        }
        else if (InstrumentEngine.EffectiveInstrumentId(instrument).Length > 0)
        {
            candles = await history.GetDailyBarsAsync(
                InstrumentEngine.EffectiveInstrumentId(instrument), 700, ct).ConfigureAwait(false);
        }
        else
        {
            candles = Array.Empty<Candle>();
        }

        var closedTrades = await tradeLogs.GetAllByInstrumentAsync(instrument.Id, ct).ConfigureAwait(false);
        var trades = closedTrades.Select(t => new ChartTradeDto(
            t.Direction == SignalDirection.Long ? "Long" : "Short",
            t.EntryTime,
            t.EntryPrice,
            t.ExitTime,
            t.ExitPrice,
            t.ExitReason.ToString(),
            t.ReturnPercent)).ToList();

        ChartOpenPositionDto? open = null;
        var openPosition = await positions.GetOpenAsync(instrument.Id, ct).ConfigureAwait(false);
        if (openPosition is not null)
        {
            open = new ChartOpenPositionDto(
                openPosition.Direction == SignalDirection.Long ? "Long" : "Short",
                openPosition.EntryPrice,
                openPosition.StopLoss,
                openPosition.TakeProfit,
                openPosition.Units,
                openPosition.EntryTime);
        }

        // Anchoring: a signal logged on a still-in-progress day must sit on
        // that day's candle, even when the minute feed has not delivered a bar
        // yet (engine restarted mid-session). Bar times carry the Moscow offset
        // while trade times come back from Postgres in UTC, so every day
        // comparison goes through MoscowClock.ToMoscowDate.
        var historyInstrumentId = InstrumentEngine.EffectiveInstrumentId(instrument);
        var candleList = candles.ToList();
        DateOnly today = MoscowClock.ToMoscowDate(now);
        bool hasTodayCandle = candleList.Any(c => MoscowClock.ToMoscowDate(c.Time) == today);
        if (!hasTodayCandle && historyInstrumentId.Length > 0)
        {
            var todayBar = await history.GetCurrentDayBarAsync(historyInstrumentId, ct).ConfigureAwait(false);
            if (todayBar is not null)
            {
                candleList.Add(todayBar.Value);
            }
        }

        // Days referenced by signals, as placeholders when the API has no bar
        // for them (no price data at all): the marker still gets its own column.
        var referencedDays = new SortedSet<DateOnly>();
        foreach (var t in closedTrades)
        {
            referencedDays.Add(MoscowClock.ToMoscowDate(t.EntryTime));
            referencedDays.Add(MoscowClock.ToMoscowDate(t.ExitTime));
        }

        if (openPosition is not null)
        {
            referencedDays.Add(MoscowClock.ToMoscowDate(openPosition.EntryTime));
        }

        var coveredDays = new HashSet<DateOnly>(candleList.Select(c => MoscowClock.ToMoscowDate(c.Time)));
        DateOnly lastDay = candleList.Count > 0 ? MoscowClock.ToMoscowDate(candleList[^1].Time) : default;
        var placeholders = new List<(DateOnly Day, decimal Price)>();
        foreach (var day in referencedDays)
        {
            if (coveredDays.Contains(day) || (candleList.Count > 0 && day <= lastDay))
            {
                continue;
            }

            decimal refPrice = candleList.Count > 0 ? candleList[^1].Close : 0m;
            placeholders.Add((day, refPrice));
            coveredDays.Add(day);
        }

        var chartCandles = candleList
            .Select(c => new ChartCandleDto(c.Time, c.Open, c.High, c.Low, c.Close, c.Volume))
            .ToList();
        foreach (var (day, price) in placeholders)
        {
            var placeholderTime = new DateTimeOffset(
                day.ToDateTime(TimeOnly.FromTimeSpan(MoscowClock.SessionOpen)),
                MoscowClock.Offset);
            chartCandles.Add(new ChartCandleDto(placeholderTime, price, price, price, price, 0, IsPlaceholder: true));
        }

        chartCandles.Sort((a, b) => a.Time.CompareTo(b.Time));

        // Instrument details below the chart.
        decimal realizedPnl = await tradeLogs.SumRealizedPnlAsync(instrument.Id, ct).ConfigureAwait(false);

        // Unrealized PnL prefers the live engine mark (intraday); a paused or
        // absent engine falls back to mark-to-market on the last daily close
        // with the same net-of-commission accounting the equity curve uses.
        var liveMetrics = metrics.Get(instrument.Id);
        decimal? unrealizedPnl = null;
        decimal? unrealizedPct = null;
        if (openPosition is not null)
        {
            if (liveMetrics?.PositionPnl is { } rub)
            {
                unrealizedPnl = rub;
                unrealizedPct = liveMetrics.PositionPnlPercent;
            }
            else if (candleList.Count > 0)
            {
                decimal close = candleList[^1].Close;
                int dir = (int)openPosition.Direction;
                decimal gross = (close - openPosition.EntryPrice) * openPosition.Units * dir * options.PointRub;
                decimal commission = options.CommissionPct / 100m;
                unrealizedPnl = gross
                                - close * openPosition.Units * commission
                                - openPosition.EntryPrice * openPosition.Units * commission;
                decimal notional = openPosition.EntryPrice * openPosition.Units * options.PointRub;
                unrealizedPct = notional > 0 ? unrealizedPnl / notional * 100m : 0m;
            }
        }

        decimal totalPnl = realizedPnl + (unrealizedPnl ?? 0m);
        decimal pool = options.InitialCapital;
        decimal totalPct = pool > 0 ? totalPnl / pool * 100m : 0m;

        var recentSignals = await signals.GetByInstrumentAsync(instrument.Id, RecentSignalsLimit, ct).ConfigureAwait(false);

        return new InstrumentDetailsDto(
            instrument.Ticker,
            instrument.Name ?? string.Empty,
            chartCandles,
            trades,
            open,
            realizedPnl,
            unrealizedPnl,
            unrealizedPct,
            totalPnl,
            totalPct,
            liveMetrics?.PendingSignal,
            recentSignals);
    }
}