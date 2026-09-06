using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioTradingSystem.Application.Configuration;
using PortfolioTradingSystem.Application.Ports;
using PortfolioTradingSystem.Domain.Enums;
using PortfolioTradingSystem.Domain.Models;
using PortfolioTradingSystem.Domain.Strategy;

namespace PortfolioTradingSystem.Application.Engine;

public sealed class EngineSupervisor : BackgroundService, IEngineSupervisor
{
    private readonly IServiceProvider _services;
    private readonly IInstrumentRepository _instruments;
    private readonly IMetricsStore _metrics;
    private readonly StrategyOptions _strategyOptions;
    private readonly EngineOptions _engineOptions;
    private readonly ILogger<EngineSupervisor> _logger;

    private readonly Dictionary<Guid, InstrumentEngine> _engines = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    private volatile bool _globallyRunning = true;

    public EngineSupervisor(
        IServiceProvider services,
        IInstrumentRepository instruments,
        IMetricsStore metrics,
        IOptions<StrategyOptions> strategyOptions,
        IOptions<EngineOptions> engineOptions,
        ILogger<EngineSupervisor> logger)
    {
        _services = services;
        _instruments = instruments;
        _metrics = metrics;
        _strategyOptions = strategyOptions.Value;
        _engineOptions = engineOptions.Value;
        _logger = logger;
    }

    public IReadOnlyList<InstrumentEngine> Engines
    {
        get
        {
            lock (_gate)
            {
                return _engines.Values.ToList();
            }
        }
    }

    public bool IsGloballyRunning => _globallyRunning;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_globallyRunning)
                    {
                        await SyncInstrumentsAsync(stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await StopStaleEnginesAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Engine supervisor sync failed");
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _engineOptions.RefreshInstrumentsIntervalSeconds)), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Engine supervisor shutting down");
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        List<InstrumentEngine> engines;
        lock (_gate)
        {
            engines = _engines.Values.ToList();
            _engines.Clear();
        }

        foreach (var engine in engines)
        {
            await engine.StopAsync().ConfigureAwait(false);
        }
    }

    public async Task SyncInstrumentsAsync(CancellationToken ct)
    {
        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await _instruments.GetAllAsync(ct).ConfigureAwait(false);
            var wanted = all
                .Where(i => i.ProcessingStatus == ProcessingStatus.Running
                            && (!string.IsNullOrWhiteSpace(i.Figi) || !string.IsNullOrWhiteSpace(i.Uid)))
                .ToList();
            var wantedIds = wanted.Select(i => i.Id).ToHashSet();
            var existingIds = all.Select(i => i.Id).ToHashSet();

            List<InstrumentEngine> toStop = new();
            List<InstrumentEngine> toStart = new();

            lock (_gate)
            {
                foreach (var id in _engines.Keys.ToList())
                {
                    var engine = _engines[id];
                    if (!wantedIds.Contains(id))
                    {
                        toStop.Add(engine);
                        _engines.Remove(id);
                        if (!existingIds.Contains(id))
                        {
                            _metrics.Remove(id);
                        }
                    }
                }

                foreach (var instrument in wanted)
                {
                    if (_engines.TryGetValue(instrument.Id, out var engine))
                    {
                        if (engine.TinkoffInstrumentId == (instrument.Uid ?? instrument.Figi))
                        {
                            continue;
                        }

                        _logger.LogInformation(
                            "Engine {Ticker} instrument id changed to {NewId}; restarting",
                            instrument.Ticker, instrument.Uid ?? instrument.Figi);
                        toStop.Add(engine);
                        _engines.Remove(instrument.Id);
                    }

                    var fresh = CreateEngine(instrument);
                    _engines[instrument.Id] = fresh;
                    toStart.Add(fresh);
                }
            }

            foreach (var engine in toStop)
            {
                await engine.StopAsync().ConfigureAwait(false);
            }

            foreach (var engine in toStart)
            {
                engine.Start();
            }

            if (toStop.Count > 0 || toStart.Count > 0)
            {
                _logger.LogInformation(
                    "Engine sync applied: {Stopped} stopped, {Started} started (wanted {Wanted})",
                    toStop.Count, toStart.Count, wanted.Count);
            }
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task StopAllAsync(CancellationToken ct)
    {
        _logger.LogInformation("Global stop: pausing all engines");
        _globallyRunning = false;
        await _instruments.SetProcessingStatusAsync(ProcessingStatus.Paused, ct).ConfigureAwait(false);
        await SyncInstrumentsAsync(ct).ConfigureAwait(false);
    }

    public async Task ResumeAllAsync(CancellationToken ct)
    {
        _logger.LogInformation("Global resume: starting all active engines");
        _globallyRunning = true;
        await _instruments.SetProcessingStatusAsync(ProcessingStatus.Running, ct).ConfigureAwait(false);
        await SyncInstrumentsAsync(ct).ConfigureAwait(false);
    }

    public async Task ResetAllAsync(CancellationToken ct)
    {
        _logger.LogInformation("Global reset: clearing metrics and restarting engines");
        _globallyRunning = true;
        await StopStaleEnginesAsync(ct).ConfigureAwait(false);
        _metrics.RemoveAll();
        await SyncInstrumentsAsync(ct).ConfigureAwait(false);
    }

    private async Task StopStaleEnginesAsync(CancellationToken ct)
    {
        List<InstrumentEngine> engines;
        lock (_gate)
        {
            engines = _engines.Values.ToList();
            _engines.Clear();
        }

        foreach (var engine in engines)
        {
            await engine.StopAsync().ConfigureAwait(false);
        }
    }

    private InstrumentEngine CreateEngine(Instrument instrument) =>
        ActivatorUtilities.CreateInstance<InstrumentEngine>(_services, instrument, _strategyOptions);
}