using Microsoft.Extensions.Options;
using PortfolioTradingSystem.Application.Engine;
using PortfolioTradingSystem.Application.Ports;
using PortfolioTradingSystem.Domain.Enums;
using PortfolioTradingSystem.Domain.Models;
using PortfolioTradingSystem.Domain.Strategy;

namespace PortfolioTradingSystem.Api.Admin;

/// <summary>Admin panel HTTP surface (Basic Auth applied for the whole app).</summary>
public static class AdminEndpoints
{
    public static WebApplication MapAdminEndpoints(this WebApplication app)
    {
        const string rootPrefix = "/api";
        var logger = app.Logger;

        app.MapGet("/admin", AdminPage.Serve);

        app.MapGet("/chart", ChartPage.Serve);

        app.MapGet("/equity", EquityPage.Serve);

        app.MapGet($"{rootPrefix}/equity-data", async (
            IInstrumentRepository instruments,
            IEngineSupervisor supervisor,
            IMetricsStore metrics,
            ITradeLogRepository tradeLogs,
            IPositionRepository positions,
            IHistoricDailyBarsProvider history,
            IOptions<StrategyOptions> strategyOptions,
            CancellationToken ct) =>
        {
            var all = await instruments.GetAllAsync(ct).ConfigureAwait(false);
            var opt = strategyOptions.Value;
            decimal pool = all.Count * opt.InitialCapital;
            decimal commission = opt.CommissionPct / 100m;
            var allTrades = await tradeLogs.GetAllAsync(ct).ConfigureAwait(false);
            var tradesByTicker = allTrades
                .GroupBy(t => t.Ticker)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var openByTicker = new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in all)
            {
                var open = await positions.GetOpenAsync(inst.Id, ct).ConfigureAwait(false);
                if (open is not null)
                {
                    openByTicker[inst.Ticker] = open;
                }
            }

            var closeByDateByTicker = new Dictionary<string, SortedDictionary<DateTime, decimal>>(StringComparer.OrdinalIgnoreCase);
            var timeByDay = new SortedDictionary<DateTime, DateTimeOffset>();
            var daySet = new SortedSet<DateTime>();
            void AddDay(DateTimeOffset time)
            {
                var date = time.DateTime.Date;
                daySet.Add(date);
                if (!timeByDay.ContainsKey(date))
                {
                    timeByDay[date] = time;
                }
            }

            foreach (var inst in all)
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
                else if (!string.IsNullOrWhiteSpace(inst.Uid ?? inst.Figi))
                {
                    try
                    {
                        bars = await history.GetDailyBarsAsync(inst.Uid ?? inst.Figi!, 700, ct).ConfigureAwait(false);
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

                var byDate = new SortedDictionary<DateTime, decimal>();
                foreach (var c in bars)
                {
                    AddDay(c.Time);
                    byDate[c.Time.DateTime.Date] = c.Close;
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

            decimal MarkToMarket(DateTime day, decimal close, int dir, decimal entryPrice, int units)
            {
                decimal gross = (close - entryPrice) * units * dir * opt.PointRub;
                decimal closeCommission = close * units * commission;
                decimal openCommission = entryPrice * units * commission;
                return gross - closeCommission - openCommission;
            }

            decimal RealizedUpTo(DateTime day)
            {
                decimal sum = 0m;
                foreach (var t in allTrades)
                {
                    if (t.ExitTime.DateTime.Date < day)
                    {
                        sum += t.PnlRub;
                    }
                }

                return sum;
            }

            decimal UnrealizedOn(DateTime day)
            {
                decimal sum = 0m;
                foreach (var kvp in closeByDateByTicker)
                {
                    if (!kvp.Value.TryGetValue(day, out decimal close))
                    {
                        continue;
                    }

                    if (tradesByTicker.TryGetValue(kvp.Key, out var tickerTrades))
                    {
                        foreach (var t in tickerTrades)
                        {
                            var entryDay = t.EntryTime.DateTime.Date;
                            var exitDay = t.ExitTime.DateTime.Date;
                            if (entryDay <= day && day <= exitDay)
                            {
                                sum += MarkToMarket(day, close, (int)t.Direction, t.EntryPrice, t.Units);
                            }
                        }
                    }

                    if (openByTicker.TryGetValue(kvp.Key, out var op) && op.EntryTime.DateTime.Date <= day)
                    {
                        sum += MarkToMarket(day, close, (int)op.Direction, op.EntryPrice, op.Units);
                    }
                }

                return sum;
            }

            static DateTime? MaybeEarlier(DateTime? current, DateTime candidate) =>
                current is null || candidate < current ? candidate : current;

            DateTime? startDay = null;
            foreach (var trade in allTrades)
            {
                startDay = MaybeEarlier(startDay, trade.EntryTime.DateTime.Date);
                startDay = MaybeEarlier(startDay, trade.ExitTime.DateTime.Date);
            }

            foreach (var open in openByTicker.Values)
            {
                startDay = MaybeEarlier(startDay, open.EntryTime.DateTime.Date);
            }

            if (startDay is null && daySet.Count > 0)
            {
                startDay = daySet.Max();
            }

            var points = new List<EquityPointDto>(daySet.Count + 1);
            foreach (var day in daySet)
            {
                if (startDay is { } start && day < start)
                {
                    continue;
                }

                decimal equity = pool + RealizedUpTo(day) + UnrealizedOn(day);
                points.Add(new EquityPointDto(timeByDay[day], equity));
            }

            decimal totalRealized = allTrades.Sum(t => t.PnlRub);
            decimal unrl = metrics.GetAll().Sum(m => m.PositionPnl ?? 0m);
            if (points.Count == 0)
            {
                points.Add(new EquityPointDto(DateTimeOffset.Now, pool + unrl));
            }
            else if (points[^1].Time < DateTimeOffset.Now)
            {
                points.Add(new EquityPointDto(DateTimeOffset.Now, pool + totalRealized + unrl));
            }

            return Results.Json(new EquityDataDto(
                points,
                pool,
                totalRealized,
                unrl,
                pool + totalRealized + unrl));
        });

        app.MapGet("/", async (IInstrumentRepository instruments, IEngineSupervisor supervisor, IMetricsStore metrics, ITradeLogRepository tradeLogs, IOptions<StrategyOptions> strategyOptions, CancellationToken ct) =>
        {
            var all = await instruments.GetAllAsync(ct).ConfigureAwait(false);
            var running = all.Count(i => i.ProcessingStatus == ProcessingStatus.Running);
            var metricsList = metrics.GetAll();
            decimal realized = await tradeLogs.SumAllRealizedPnlAsync(ct).ConfigureAwait(false);
            decimal open = metricsList.Sum(m => m.PositionPnl ?? 0m);
            decimal total = realized + open;
            decimal pool = all.Count * strategyOptions.Value.InitialCapital;
            decimal pct = pool > 0 ? total / pool * 100m : 0m;
            return Results.Text(
                $"Portfolio Trading System\n" +
                $"instruments: {all.Count} (running: {running})\n" +
                $"engines running: {supervisor.Engines.Count}\n" +
                $"global state: {(supervisor.IsGloballyRunning ? "running" : "stopped")}\n" +
                $"metrics tracked: {metricsList.Count}\n" +
                $"total pnl: {total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}\n" +
                $"total pnl pct: {pct.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
        });

        app.MapGet($"{rootPrefix}/metrics", (IMetricsStore metrics) =>
            Results.Json(metrics.GetAll()));

        app.MapGet($"{rootPrefix}/instruments", async (IInstrumentRepository instruments, IMetricsStore metrics, CancellationToken ct) =>
        {
            var all = await instruments.GetAllAsync(ct).ConfigureAwait(false);
            var ordered = all
                .OrderBy(i => i.ProcessingStatus == ProcessingStatus.Running ? 0 : 1)
                .ThenBy(i => i.Ticker)
                .ToList();
            return Results.Json(ordered.Select(i => new InstrumentView(i, metrics.Get(i.Id))));
        });

        app.MapGet($"{rootPrefix}/instruments/{{id:guid}}", async (Guid id, IInstrumentRepository instruments, IMetricsStore metrics, CancellationToken ct) =>
        {
            var instrument = await instruments.GetByIdAsync(id, ct).ConfigureAwait(false);
            return instrument is null
                ? Results.NotFound()
                : Results.Json(new InstrumentView(instrument, metrics.Get(id)));
        });

        app.MapGet($"{rootPrefix}/instruments/{{id:guid}}/chart-data", async (
            Guid id,
            IInstrumentRepository instruments,
            IEngineSupervisor supervisor,
            IHistoricDailyBarsProvider history,
            ITradeLogRepository tradeLogs,
            IPositionRepository positions,
            CancellationToken ct) =>
        {
            var instrument = await instruments.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (instrument is null)
            {
                return Results.NotFound();
            }

            IReadOnlyList<Candle> candles;
            var engine = supervisor.GetEngine(id);
            if (engine is not null)
            {
                var bars = engine.GetDailyBars().ToList();
                if (engine.CurrentSessionBar is { } live && (bars.Count == 0 || bars[^1].Time < live.Time))
                {
                    bars.Add(live);
                }

                candles = bars;
            }
            else if (!string.IsNullOrWhiteSpace(instrument.Uid ?? instrument.Figi))
            {
                candles = await history.GetDailyBarsAsync(instrument.Uid ?? instrument.Figi!, 700, ct).ConfigureAwait(false);
            }
            else
            {
                candles = Array.Empty<Candle>();
            }

            var closedTrades = await tradeLogs.GetAllByInstrumentAsync(id, ct).ConfigureAwait(false);
            var trades = closedTrades.Select(t => new ChartTradeDto(
                t.Direction == SignalDirection.Long ? "Long" : "Short",
                t.EntryTime,
                t.EntryPrice,
                t.ExitTime,
                t.ExitPrice,
                t.ExitReason.ToString(),
                t.ReturnPercent)).ToList();

            ChartOpenPositionDto? open = null;
            var openPosition = await positions.GetOpenAsync(id, ct).ConfigureAwait(false);
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

            return Results.Json(new ChartDataDto(
                instrument.Ticker,
                instrument.Name ?? string.Empty,
                candles.Select(c => new ChartCandleDto(c.Time, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList(),
                trades,
                open));
        });

        app.MapGet($"{rootPrefix}/instruments/{{id:guid}}/signals", async (Guid id, ISignalLogRepository signals, int limit, CancellationToken ct) =>
        {
            if (limit <= 0 || limit > 500)
            {
                limit = 100;
            }

            return Results.Json(await signals.GetByInstrumentAsync(id, limit, ct).ConfigureAwait(false));
        });

        app.MapGet($"{rootPrefix}/signals", async (ISignalLogRepository signals, string? ticker, int page, int pageSize, CancellationToken ct) =>
        {
            page = page <= 0 ? 1 : page;
            pageSize = pageSize <= 0 || pageSize > 200 ? 50 : pageSize;
            return Results.Json(await signals.GetPageAsync(ticker, page, pageSize, ct).ConfigureAwait(false));
        });

        app.MapGet($"{rootPrefix}/signals/tickers", async (ISignalLogRepository signals, CancellationToken ct) =>
            Results.Json(await signals.GetTickersAsync(ct).ConfigureAwait(false)));

        app.MapPost($"{rootPrefix}/signals/{{id:guid}}/resend", async (Guid id, ISignalLogRepository signals, ITelegramGateway telegram, TelegramMessageFormatter formatter, CancellationToken ct) =>
        {
            var entry = await signals.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (entry is null)
            {
                logger.LogWarning("Signal resend failed: {SignalId} not found", id);
                return Results.NotFound();
            }

            await telegram.SendMessageAsync(formatter.Duplicate(entry), ct).ConfigureAwait(false);
            logger.LogInformation("Resent signal {SignalId} ({Ticker} {Type}) to Telegram", entry.Id, entry.Ticker, entry.Type);
            return Results.Json(entry);
        });

        app.MapPost($"{rootPrefix}/instruments/{{id:guid}}/resend-active", async (Guid id, ISignalLogRepository signals, ITelegramGateway telegram, TelegramMessageFormatter formatter, CancellationToken ct) =>
        {
            var entry = await signals.GetLatestOpenedAsync(id, ct).ConfigureAwait(false);
            if (entry is null)
            {
                logger.LogWarning("Active signal resend failed: no PositionOpened for {InstrumentId}", id);
                return Results.NotFound();
            }

            await telegram.SendMessageAsync(formatter.Duplicate(entry), ct).ConfigureAwait(false);
            logger.LogInformation("Resent active signal {SignalId} ({Ticker}) to Telegram", entry.Id, entry.Ticker);
            return Results.Json(entry);
        });

        app.MapPost($"{rootPrefix}/instruments", async (InstrumentUpsertDto dto, IInstrumentRepository instruments, IEngineSupervisor supervisor, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Ticker))
            {
                logger.LogWarning("Rejected instrument create (empty ticker)");
                return Results.ValidationProblem(new Dictionary<string, string[]> { { "ticker", new[] { "Ticker is required." } } });
            }

            string ticker = dto.Ticker.Trim().ToUpperInvariant();
            if (await instruments.FindByTickerAsync(ticker, ct).ConfigureAwait(false) is not null)
            {
                logger.LogWarning("Rejected instrument create: {Ticker} already exists", ticker);
                return Results.Conflict($"Instrument {ticker} already exists.");
            }

            var instrument = new Instrument
            {
                Ticker = ticker,
                Isin = dto.Isin,
                Figi = dto.Figi,
                Uid = dto.Uid,
                ClassCode = dto.ClassCode,
                Name = dto.Name,
                LotSize = dto.LotSize ?? 1,
                FutureTicker = dto.FutureTicker,
                FutureFigi = dto.FutureFigi,
                FutureClassCode = dto.FutureClassCode,
                ProcessingStatus = ParseProcessingStatus(dto.ProcessingStatus),
            };
            await instruments.AddAsync(instrument, ct).ConfigureAwait(false);
            await supervisor.SyncInstrumentsAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Created instrument {InstrumentId} ({Ticker})", instrument.Id, instrument.Ticker);
            return Results.Created($"/api/instruments/{instrument.Id}", instrument);
        });

        app.MapPut($"{rootPrefix}/instruments/{{id:guid}}", async (Guid id, InstrumentUpsertDto dto, IInstrumentRepository instruments, IEngineSupervisor supervisor, CancellationToken ct) =>
        {
            var instrument = await instruments.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (instrument is null)
            {
                logger.LogWarning("Instrument update failed: {InstrumentId} not found", id);
                return Results.NotFound();
            }

            if (!string.IsNullOrWhiteSpace(dto.Ticker))
            {
                string ticker = dto.Ticker.Trim().ToUpperInvariant();
                var other = await instruments.FindByTickerAsync(ticker, ct).ConfigureAwait(false);
                if (other is not null && other.Id != id)
                {
                    logger.LogWarning("Instrument update rejected: {Ticker} already exists on {OtherId}", ticker, other.Id);
                    return Results.Conflict($"Instrument {ticker} already exists.");
                }

                instrument.Ticker = ticker;
            }

            instrument.Isin = dto.Isin;
            instrument.Figi = dto.Figi;
            instrument.Uid = dto.Uid;
            instrument.ClassCode = dto.ClassCode;
            instrument.Name = dto.Name;
            if (dto.LotSize is not null)
            {
                instrument.LotSize = dto.LotSize.Value;
            }

            instrument.FutureTicker = dto.FutureTicker;
            instrument.FutureFigi = dto.FutureFigi;
            instrument.FutureClassCode = dto.FutureClassCode;
            if (!string.IsNullOrWhiteSpace(dto.ProcessingStatus))
            {
                instrument.ProcessingStatus = ParseProcessingStatus(dto.ProcessingStatus);
            }

            await instruments.UpdateAsync(instrument, ct).ConfigureAwait(false);
            await supervisor.SyncInstrumentsAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Updated instrument {InstrumentId} ({Ticker})", instrument.Id, instrument.Ticker);
            return Results.Json(instrument);
        });

        app.MapDelete($"{rootPrefix}/instruments/{{id:guid}}", async (Guid id, IInstrumentRepository instruments, IEngineSupervisor supervisor, CancellationToken ct) =>
        {
            bool deleted = await instruments.DeleteAsync(id, ct).ConfigureAwait(false);
            if (deleted)
            {
                await supervisor.SyncInstrumentsAsync(ct).ConfigureAwait(false);
                logger.LogInformation("Deleted instrument {InstrumentId}", id);
                return Results.NoContent();
            }

            logger.LogWarning("Instrument delete failed: {InstrumentId} not found", id);
            return Results.NotFound();
        });

        app.MapPost($"{rootPrefix}/instruments/{{id:guid}}/resolve", async (Guid id, IInstrumentRepository instruments, ITickerResolver resolver, CancellationToken ct) =>
        {
            var instrument = await instruments.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (instrument is null)
            {
                logger.LogWarning("Resolve failed: {InstrumentId} not found", id);
                return Results.NotFound();
            }

            var resolution = await resolver.ResolveAsync(instrument.Ticker, ct).ConfigureAwait(false);
            switch (resolution.Status)
            {
                case TickerResolutionStatus.NotFound:
                    logger.LogWarning("Resolve {Ticker}: {Message}", instrument.Ticker, resolution.Message);
                    return Results.Json(
                        new { error = "not_found", message = resolution.Message },
                        statusCode: StatusCodes.Status404NotFound);

                case TickerResolutionStatus.Ambiguous:
                    logger.LogWarning(
                        "Resolve {Ticker}: ambiguous, {Count} candidates ({Codes})",
                        instrument.Ticker, resolution.Candidates?.Count ?? 0,
                        string.Join(",", resolution.Candidates?.Select(c => c.ClassCode) ?? Array.Empty<string>()));
                    return Results.Json(
                        new { error = "ambiguous", ticker = instrument.Ticker, message = resolution.Message, candidates = resolution.Candidates },
                        statusCode: StatusCodes.Status409Conflict);

                default:
                {
                    var c = resolution.Candidate!;
                    instrument.Uid = c.Uid;
                    instrument.Figi = string.IsNullOrWhiteSpace(c.Figi) ? instrument.Figi : c.Figi;
                    instrument.Isin = string.IsNullOrWhiteSpace(c.Isin) ? instrument.Isin : c.Isin;
                    instrument.ClassCode = string.IsNullOrWhiteSpace(c.ClassCode) ? instrument.ClassCode : c.ClassCode;
                    instrument.Name = string.IsNullOrWhiteSpace(c.Name) ? instrument.Name : c.Name;
                    if (c.Lot > 0)
                    {
                        instrument.LotSize = c.Lot;
                    }

                    await instruments.UpdateAsync(instrument, ct).ConfigureAwait(false);
                    logger.LogInformation(
                        "Resolved {Ticker}: uid={Uid} figi={Figi} class={ClassCode} lot={Lot}",
                        instrument.Ticker, c.Uid, c.Figi, c.ClassCode, c.Lot);
                    return Results.Json(instrument);
                }
            }
        });

        app.MapPost($"{rootPrefix}/instruments/resolve-all", async (IInstrumentRepository instruments, IEngineSupervisor supervisor, ITickerResolver resolver, CancellationToken ct) =>
        {
            var all = await instruments.GetAllAsync(ct).ConfigureAwait(false);
            var pending = all.Where(i => string.IsNullOrWhiteSpace(i.Uid)).ToList();
            int resolvedCount = 0;
            var failed = new List<object>();
            foreach (var instrument in pending)
            {
                var resolution = await resolver.ResolveAsync(instrument.Ticker, ct).ConfigureAwait(false);
                if (resolution.Status == TickerResolutionStatus.Resolved)
                {
                    var c = resolution.Candidate!;
                    instrument.Uid = c.Uid;
                    instrument.Figi = string.IsNullOrWhiteSpace(c.Figi) ? instrument.Figi : c.Figi;
                    instrument.Isin = string.IsNullOrWhiteSpace(c.Isin) ? instrument.Isin : c.Isin;
                    instrument.ClassCode = string.IsNullOrWhiteSpace(c.ClassCode) ? instrument.ClassCode : c.ClassCode;
                    instrument.Name = string.IsNullOrWhiteSpace(c.Name) ? instrument.Name : c.Name;
                    if (c.Lot > 0)
                    {
                        instrument.LotSize = c.Lot;
                    }

                    await instruments.UpdateAsync(instrument, ct).ConfigureAwait(false);
                    resolvedCount++;
                    logger.LogInformation(
                        "Resolve-all: {Ticker}: uid={Uid} figi={Figi} class={ClassCode} lot={Lot}",
                        instrument.Ticker, c.Uid, c.Figi, c.ClassCode, c.Lot);
                }
                else
                {
                    failed.Add(new { ticker = instrument.Ticker, error = resolution.Status.ToString(), message = resolution.Message });
                    logger.LogWarning(
                        "Resolve-all: {Ticker}: {Status} — {Message}",
                        instrument.Ticker, resolution.Status, resolution.Message);
                }
            }

            await supervisor.SyncInstrumentsAsync(ct).ConfigureAwait(false);
            logger.LogInformation(
                "Resolve-all: {Resolved} resolved, {Failed} failed of {Pending} pending",
                resolvedCount, failed.Count, pending.Count);
            return Results.Json(new { pending = pending.Count, resolved = resolvedCount, failed });
        });

        app.MapPost($"{rootPrefix}/instruments/{{id:guid}}/pause", async (Guid id, IInstrumentRepository instruments, IEngineSupervisor supervisor, CancellationToken ct) =>
        {
            var instrument = await instruments.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (instrument is null)
            {
                logger.LogWarning("Pause failed: {InstrumentId} not found", id);
                return Results.NotFound();
            }

            instrument.ProcessingStatus = ProcessingStatus.Paused;
            await instruments.UpdateAsync(instrument, ct).ConfigureAwait(false);
            await supervisor.SyncInstrumentsAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Paused instrument {InstrumentId} ({Ticker})", instrument.Id, instrument.Ticker);
            return Results.Json(instrument);
        });

        app.MapPost($"{rootPrefix}/instruments/{{id:guid}}/resume", async (Guid id, IInstrumentRepository instruments, IEngineSupervisor supervisor, CancellationToken ct) =>
        {
            var instrument = await instruments.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (instrument is null)
            {
                logger.LogWarning("Resume failed: {InstrumentId} not found", id);
                return Results.NotFound();
            }

            instrument.ProcessingStatus = ProcessingStatus.Running;
            await instruments.UpdateAsync(instrument, ct).ConfigureAwait(false);
            await supervisor.SyncInstrumentsAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Resumed instrument {InstrumentId} ({Ticker})", instrument.Id, instrument.Ticker);
            return Results.Json(instrument);
        });

        app.MapPost($"{rootPrefix}/control/stop", async (IEngineSupervisor supervisor, CancellationToken ct) =>
        {
            logger.LogInformation("Control action: global stop requested");
            await supervisor.StopAllAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { status = "stopped" });
        });

        app.MapPost($"{rootPrefix}/control/resume", async (IEngineSupervisor supervisor, CancellationToken ct) =>
        {
            logger.LogInformation("Control action: global resume requested");
            await supervisor.ResumeAllAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { status = "running" });
        });

        app.MapPost($"{rootPrefix}/control/reset", async (IEngineSupervisor supervisor, CancellationToken ct) =>
        {
            logger.LogInformation("Control action: engine reset requested");
            await supervisor.ResetAllAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { status = "reset" });
        });

        return app;
    }

    private static ProcessingStatus ParseProcessingStatus(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "running" or "1" => ProcessingStatus.Running,
            _ => ProcessingStatus.Paused,
        };
}