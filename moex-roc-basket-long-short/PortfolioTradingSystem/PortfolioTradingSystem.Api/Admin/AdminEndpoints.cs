using Microsoft.Extensions.Options;
using PortfolioTradingSystem.Application.Admin;
using PortfolioTradingSystem.Application.Engine;
using PortfolioTradingSystem.Application.Ports;
using PortfolioTradingSystem.Domain.Enums;
using PortfolioTradingSystem.Domain.Models;
using PortfolioTradingSystem.Domain.Strategy;

namespace PortfolioTradingSystem.Api.Admin;

/// <summary>Admin panel HTTP surface (Basic Auth applied for the whole app).</summary>
public static class AdminEndpoints
{
    // Shared static assets served from the assembly. The pages link these by
    // absolute URL (/admin/<file>), so the routes must stay under /admin/.
    private static readonly Dictionary<string, string> AdminAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["app.css"] = "text/css",
        ["pts.js"] = "text/javascript",
        ["chart.js"] = "text/javascript",
        ["admin.js"] = "text/javascript",
        ["details.js"] = "text/javascript",
        ["equity.js"] = "text/javascript",
    };

    public static WebApplication MapAdminEndpoints(this WebApplication app)
    {
        const string rootPrefix = "/api";
        var logger = app.Logger;

        app.MapGet("/admin", () =>
            Results.Content(
                EmbeddedAssets.ReadText("admin.html").Replace("$BUILD_LABEL$", EmbeddedAssets.BuildLabel()),
                "text/html"));

        app.MapGet("/details", () => Results.Content(EmbeddedAssets.ReadText("details.html"), "text/html"));

        app.MapGet("/equity", () => Results.Content(EmbeddedAssets.ReadText("equity.html"), "text/html"));

        app.MapGet("/admin/{file}", (string file) =>
            AdminAssets.TryGetValue(file, out string? contentType)
                ? Results.Content(EmbeddedAssets.ReadText(file), contentType)
                : Results.NotFound());

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
            var dto = await EquityCurveBuilder.BuildAsync(
                all, supervisor, history, tradeLogs, positions, metrics,
                strategyOptions.Value, DateTimeOffset.Now, ct).ConfigureAwait(false);
            return Results.Json(dto);
        });

        // Per-instrument equity curve for the details page (same daily curve as the
        // portfolio endpoint, but scaled to one instrument's InitialCapital).
        app.MapGet($"{rootPrefix}/instruments/{{id:guid}}/equity-data", async (
            Guid id,
            IInstrumentRepository instruments,
            IEngineSupervisor supervisor,
            IMetricsStore metrics,
            ITradeLogRepository tradeLogs,
            IPositionRepository positions,
            IHistoricDailyBarsProvider history,
            IOptions<StrategyOptions> strategyOptions,
            CancellationToken ct) =>
        {
            var instrument = await instruments.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (instrument is null)
            {
                return Results.NotFound();
            }

            var dto = await EquityCurveBuilder.BuildAsync(
                new[] { instrument }, supervisor, history, tradeLogs, positions, metrics,
                strategyOptions.Value, DateTimeOffset.Now, ct).ConfigureAwait(false);
            return Results.Json(dto);
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
            decimal openPct = pool > 0 ? open / pool * 100m : 0m;

            decimal openLong = metricsList.Where(m => m.PositionState == "long").Sum(m => m.PositionPnl ?? 0m);
            decimal openShort = metricsList.Where(m => m.PositionState == "short").Sum(m => m.PositionPnl ?? 0m);
            decimal longs = openLong;
            decimal shorts = openShort;
            decimal longsPct = pool > 0 ? longs / pool * 100m : 0m;
            decimal shortsPct = pool > 0 ? shorts / pool * 100m : 0m;

            return Results.Text(
                $"Portfolio Trading System\n" +
                $"instruments: {all.Count} (running: {running})\n" +
                $"engines running: {supervisor.Engines.Count}\n" +
                $"global state: {(supervisor.IsGloballyRunning ? "running" : "stopped")}\n" +
                $"metrics tracked: {metricsList.Count}\n" +
                $"total pnl: {total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}\n" +
                $"total pnl pct: {pct.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}\n" +
                $"total unrealized pnl: {open.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}\n" +
                $"total unrealized pnl pct: {openPct.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}\n" +
                $"longs pnl: {longs.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}\n" +
                $"longs pnl pct: {longsPct.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}\n" +
                $"shorts pnl: {shorts.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}\n" +
                $"shorts pnl pct: {shortsPct.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
        });

        // Machine-readable equivalent of GET "/" for the admin UI header/PnL cards.
        app.MapGet($"{rootPrefix}/summary", async (
            IInstrumentRepository instruments,
            IEngineSupervisor supervisor,
            IMetricsStore metrics,
            ITradeLogRepository tradeLogs,
            IOptions<StrategyOptions> strategyOptions,
            CancellationToken ct) =>
        {
            var all = await instruments.GetAllAsync(ct).ConfigureAwait(false);
            var metricsList = metrics.GetAll();
            decimal realized = await tradeLogs.SumAllRealizedPnlAsync(ct).ConfigureAwait(false);
            decimal open = metricsList.Sum(m => m.PositionPnl ?? 0m);
            decimal total = realized + open;
            decimal pool = all.Count * strategyOptions.Value.InitialCapital;
            decimal openLong = metricsList.Where(m => m.PositionState == "long").Sum(m => m.PositionPnl ?? 0m);
            decimal openShort = metricsList.Where(m => m.PositionState == "short").Sum(m => m.PositionPnl ?? 0m);
            decimal Percent(decimal value) => pool > 0 ? value / pool * 100m : 0m;

            return Results.Json(new PortfolioSummary(
                Instruments: all.Count,
                Running: all.Count(i => i.ProcessingStatus == ProcessingStatus.Running),
                Engines: supervisor.Engines.Count,
                GlobalState: supervisor.IsGloballyRunning ? "running" : "stopped",
                MetricsTracked: metricsList.Count,
                TotalPnl: total,
                TotalPnlPct: Percent(total),
                UnrealizedPnl: open,
                UnrealizedPnlPct: Percent(open),
                LongsPnl: openLong,
                LongsPnlPct: Percent(openLong),
                ShortsPnl: openShort,
                ShortsPnlPct: Percent(openShort)));
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

        app.MapGet($"{rootPrefix}/instruments/{{id:guid}}/details", async (
            Guid id,
            IInstrumentRepository instruments,
            IEngineSupervisor supervisor,
            IHistoricDailyBarsProvider history,
            ITradeLogRepository tradeLogs,
            IPositionRepository positions,
            IMetricsStore metrics,
            ISignalLogRepository signals,
            IOptions<StrategyOptions> strategyOptions,
            CancellationToken ct) =>
        {
            var instrument = await instruments.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (instrument is null)
            {
                return Results.NotFound();
            }

            var dto = await InstrumentDetailsBuilder.BuildAsync(
                instrument, supervisor, history, tradeLogs, positions, metrics, signals,
                strategyOptions.Value, ct).ConfigureAwait(false);
            return Results.Json(dto);
        });

        // Nullable with a default: a non-nullable int query parameter is REQUIRED by
        // minimal APIs, so omitting ?limit= returned 400 while the README documented
        // a default of 100.
        app.MapGet($"{rootPrefix}/instruments/{{id:guid}}/signals", async (Guid id, ISignalLogRepository signals, int? limit, CancellationToken ct) =>
        {
            int take = limit is > 0 and <= 500 ? limit.Value : 100;
            return Results.Json(await signals.GetByInstrumentAsync(id, take, ct).ConfigureAwait(false));
        });

        app.MapGet($"{rootPrefix}/signals", async (ISignalLogRepository signals, string? ticker, int? page, int? pageSize, CancellationToken ct) =>
        {
            int p = page is > 0 ? page.Value : 1;
            int size = pageSize is > 0 and <= 200 ? pageSize.Value : 50;
            return Results.Json(await signals.GetPageAsync(ticker, p, size, ct).ConfigureAwait(false));
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

        app.MapPost($"{rootPrefix}/instruments/{{id:guid}}/close", async (
            Guid id, decimal? price, IEngineSupervisor supervisor, CancellationToken ct) =>
        {
            var engine = supervisor.GetEngine(id);
            if (engine is null)
            {
                logger.LogWarning("Manual close failed: no running engine for {InstrumentId}", id);
                return Results.NotFound();
            }

            var closed = await engine.ClosePositionAsync(price, ct).ConfigureAwait(false);
            if (closed is null)
            {
                return Results.Conflict(new { error = "flat", message = "No open position to close." });
            }

            logger.LogInformation(
                "Manually closed {Ticker}: {Units} @ {Exit}, pnl={Pnl:0.00} RUB",
                closed.Ticker, closed.Units, closed.ExitPrice, closed.PnlRub);
            return Results.Json(closed);
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

/// <summary>Portfolio-level snapshot for the admin header and PnL cards (GET /api/summary).</summary>
public sealed record PortfolioSummary(
    int Instruments,
    int Running,
    int Engines,
    string GlobalState,
    int MetricsTracked,
    decimal TotalPnl,
    decimal TotalPnlPct,
    decimal UnrealizedPnl,
    decimal UnrealizedPnlPct,
    decimal LongsPnl,
    decimal LongsPnlPct,
    decimal ShortsPnl,
    decimal ShortsPnlPct);