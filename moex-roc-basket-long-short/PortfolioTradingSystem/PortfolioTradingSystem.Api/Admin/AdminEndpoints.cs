using PortfolioTradingSystem.Application.Engine;
using PortfolioTradingSystem.Application.Ports;
using PortfolioTradingSystem.Domain.Enums;
using PortfolioTradingSystem.Domain.Models;

namespace PortfolioTradingSystem.Api.Admin;

/// <summary>Admin panel HTTP surface (Basic Auth applied for the whole app).</summary>
public static class AdminEndpoints
{
    public static WebApplication MapAdminEndpoints(this WebApplication app)
    {
        const string rootPrefix = "/api";
        var logger = app.Logger;

        app.MapGet("/admin", AdminPage.Serve);

        app.MapGet("/", async (IInstrumentRepository instruments, IEngineSupervisor supervisor, IMetricsStore metrics, CancellationToken ct) =>
        {
var all = await instruments.GetAllAsync(ct).ConfigureAwait(false);
            var running = all.Count(i => i.ProcessingStatus == ProcessingStatus.Running);
            var metricsList = metrics.GetAll();
            return Results.Text(
                $"Portfolio Trading System\n" +
                $"instruments: {all.Count} (running: {running})\n" +
                $"engines running: {supervisor.Engines.Count}\n" +
                $"global state: {(supervisor.IsGloballyRunning ? "running" : "stopped")}\n" +
                $"metrics tracked: {metricsList.Count}");
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

        app.MapGet($"{rootPrefix}/instruments/{{id:guid}}/signals", async (Guid id, ISignalLogRepository signals, int limit, CancellationToken ct) =>
        {
            if (limit <= 0 || limit > 500)
            {
                limit = 100;
            }

            return Results.Json(await signals.GetByInstrumentAsync(id, limit, ct).ConfigureAwait(false));
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