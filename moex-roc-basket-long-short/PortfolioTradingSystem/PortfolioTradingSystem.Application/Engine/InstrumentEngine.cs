using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioTradingSystem.Application.Configuration;
using PortfolioTradingSystem.Application.Ports;
using PortfolioTradingSystem.Domain.Enums;
using PortfolioTradingSystem.Domain.Models;
using PortfolioTradingSystem.Domain.Strategy;

namespace PortfolioTradingSystem.Application.Engine;

/// <summary>
/// Runs the roc_momentum engine for a single instrument: warms up on historical
/// daily bars, restores persisted state, then processes the real-time 1-minute
/// candle stream (daily-bar aggregation, daily ROC entries, intraday SL/TP exits).
/// Handles reconnection with exponential backoff.
/// </summary>
public sealed class InstrumentEngine : IAsyncDisposable
{
    private readonly Instrument _instrument;
    private readonly StrategyOptions _strategyOptions;
    private readonly IMarketDataGateway _marketData;
    private readonly IHistoricDailyBarsProvider _history;
    private readonly ITelegramGateway _telegram;
    private readonly IPositionRepository _positions;
    private readonly ITradeLogRepository _tradeLogs;
    private readonly ISignalLogRepository _signalLogs;
    private readonly IMetricsStore _metrics;
    private readonly TelegramMessageFormatter _formatter;
    private readonly ILogger<InstrumentEngine> _logger;
    private readonly TimeSpan _reconnectStart;
    private readonly TimeSpan _reconnectMax;
    private readonly int _maxBars;

    private readonly MomentumEngineState _state;
    private readonly object _gate = new();
    private readonly object _barsLock = new();
    private readonly object _stateLock = new();
    private readonly List<Candle> _dailyBars = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    private Candle? _sessionBar;
    private DateOnly _sessionDate;
    private bool _enteredThisSession;
    private bool _exitedThisSession;
    private bool _sessionOpenObserved;
    private DateTimeOffset? _lastMinuteTime;
    private long _lastMinuteVolume;

    public InstrumentEngine(
        Instrument instrument,
        StrategyOptions strategyOptions,
        IMarketDataGateway marketData,
        IHistoricDailyBarsProvider history,
        ITelegramGateway telegram,
        IPositionRepository positions,
        ITradeLogRepository tradeLogs,
        ISignalLogRepository signalLogs,
        IMetricsStore metrics,
        TelegramMessageFormatter formatter,
        IOptions<EngineOptions> engineOptions,
        IOptions<TinkoffOptions> tinkoffOptions,
        ILogger<InstrumentEngine> logger)
    {
        _instrument = instrument;
        _strategyOptions = strategyOptions;
        _marketData = marketData;
        _history = history;
        _telegram = telegram;
        _positions = positions;
        _tradeLogs = tradeLogs;
        _signalLogs = signalLogs;
        _metrics = metrics;
        _formatter = formatter;
        _logger = logger;
        _reconnectStart = TimeSpan.FromSeconds(engineOptions.Value.ReconnectDelaySeconds);
        _reconnectMax = TimeSpan.FromSeconds(engineOptions.Value.ReconnectMaxDelaySeconds);
        _maxBars = Math.Max(strategyOptions.RocBars + 1, tinkoffOptions.Value.HistoricMaxBars);
        _state = new MomentumEngineState(strategyOptions, instrument.Ticker, instrument.LotSize);
        _metrics.Initialize(instrument.Id, instrument.Ticker);
    }

    public Guid InstrumentId => _instrument.Id;

    public string Ticker => _instrument.Ticker;

    /// <summary>Effective T-Bank instrument identifier the engine subscribes to (UID preferred, FIGI fallback).</summary>
    public string TinkoffInstrumentId => _instrument.Uid ?? _instrument.Figi ?? string.Empty;

    public bool IsRunning => _runTask is not null && !_runTask.IsCompleted;

    public EngineStateSnapshot? GetSnapshot(decimal? lastPrice)
    {
        lock (_stateLock)
        {
            return _state.GetSnapshot(lastPrice ?? _state.Position?.EntryPrice ?? 0m);
        }
    }

    /// <summary>Completed daily bars held in memory (warm-up history, finalized sessions), ascending.</summary>
    public IReadOnlyList<Candle> GetDailyBars()
    {
        lock (_barsLock)
        {
            return _dailyBars.ToArray();
        }
    }

    /// <summary>The live session bar currently being accumulated (not yet finalized).</summary>
    public Candle? CurrentSessionBar
    {
        get
        {
            lock (_stateLock)
            {
                return _sessionBar;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_runTask is not null && !_runTask.IsCompleted)
            {
                return;
            }

            _logger.LogInformation("Starting engine {Ticker} for {InstrumentId}", Ticker, InstrumentId);
            _cts = new CancellationTokenSource();
            _runTask = StartCoreAsync(_cts.Token);
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            task = _runTask;
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Engine {Ticker} stopped with an error", Ticker);
            }
        }

        cts.Dispose();
        _metrics.Update(InstrumentId, m =>
        {
            m.Status = InstrumentMetrics.StatusStopped;
            m.LastError = null;
        });
        _logger.LogInformation("Engine {Ticker} stopped", Ticker);
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        try
        {
            await WarmUpAndRestoreAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(TinkoffInstrumentId))
            {
                _logger.LogWarning("Engine {Ticker} has no FIGI/UID set; not subscribing", Ticker);
                return;
            }

            TimeSpan delay = _reconnectStart;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _metrics.Update(InstrumentId, m =>
                    {
                        m.Status = InstrumentMetrics.StatusStreaming;
                        m.LastRunTime = DateTimeOffset.UtcNow;
                        m.LastError = null;
                    });

                    await foreach (var minute in _marketData.SubscribeAsync(TinkoffInstrumentId, ct).ConfigureAwait(false))
                    {
                        if (ct.IsCancellationRequested)
                        {
                            break;
                        }

                        await ProcessMinuteAsync(minute, ct).ConfigureAwait(false);
                    }

                    delay = _reconnectStart;
                    if (!ct.IsCancellationRequested)
                    {
                        _logger.LogWarning("Subscription for {Ticker} ended; resubscribing...", Ticker);
                        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    _logger.LogError(ex, "Stream error for {Ticker}; reconnecting in {Delay}s", Ticker, delay.TotalSeconds);
                    _metrics.Update(InstrumentId, m =>
                    {
                        m.Status = InstrumentMetrics.StatusError;
                        m.LastError = ex.Message;
                    });
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _reconnectMax.TotalMilliseconds));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Engine {Ticker} failed", Ticker);
            _metrics.Update(InstrumentId, m =>
            {
                m.Status = InstrumentMetrics.StatusError;
                m.LastError = ex.Message;
            });
        }
        finally
        {
            _metrics.Update(InstrumentId, m => m.Status = InstrumentMetrics.StatusStopped);
        }
    }

    private async Task WarmUpAndRestoreAsync(CancellationToken ct)
    {
        _metrics.Update(InstrumentId, m =>
        {
            m.Status = InstrumentMetrics.StatusWarmingUp;
            m.WarmupDone = false;
        });

        IReadOnlyList<Candle> bars = Array.Empty<Candle>();
        if (!string.IsNullOrWhiteSpace(TinkoffInstrumentId))
        {
            bars = await _history.GetDailyBarsAsync(TinkoffInstrumentId, _maxBars, ct).ConfigureAwait(false);
        }

        lock (_stateLock)
        {
            _state.WarmUp(bars);
        }

        lock (_barsLock)
        {
            _dailyBars.Clear();
            _dailyBars.AddRange(bars);
        }

        _metrics.Update(InstrumentId, m =>
        {
            m.WarmupDone = true;
            m.WarmupBars = bars.Count;
            m.LatestClose = bars.Count > 0 ? bars[^1].Close : null;
        });

        decimal realized = await _tradeLogs.SumRealizedPnlAsync(InstrumentId, ct).ConfigureAwait(false);
        var open = await _positions.GetOpenAsync(InstrumentId, ct).ConfigureAwait(false);
        if (open is not null)
        {
            lock (_stateLock)
            {
                _state.RestorePosition(open);
            }

            // Free cash at restart must equal the simulator's cash right after the open
            // position was entered: initial capital + realized PnL of closed trades,
            // plus/minus the committed entry cost (long: -notional, short: +margin
            // credit, file the research cash mechanics) and the open-side commission.
            // Without this the restored cash (and therefore equity and risk% sizing)
            // stays overstated by the position's entry value.
            decimal entryCost =
                (int)open.Direction * open.Units * open.EntryPrice * _strategyOptions.PointRub
                + open.OpenCommission;
            lock (_stateLock)
            {
                _state.SetCash(_strategyOptions.InitialCapital + realized - entryCost);
            }

            _logger.LogInformation(
                "Restored {Ticker} position: {Direction} {Units} @ {Entry} (open {Open:O})",
                Ticker, open.Direction, open.Units, open.EntryPrice, open.EntryTime);
        }
        else
        {
            lock (_stateLock)
            {
                _state.SetCash(_strategyOptions.InitialCapital + realized);
            }
        }

        EngineStateSnapshot restoreSnapshot;
        OpenPositionPnl? restorePnl;
        lock (_stateLock)
        {
            decimal restoreMark = _state.Position is not null
                ? (bars.Count > 0 ? bars[^1].Close : _state.Position.EntryPrice)
                : 0m;
            restoreSnapshot = _state.GetSnapshot(restoreMark);
            restorePnl = _state.UnrealizedPnl(restoreMark);
        }

        _metrics.Update(InstrumentId, m => ApplyPositionMetrics(m, restoreSnapshot, restorePnl));

        string lastBar = bars.Count > 0
            ? bars[^1].Time.ToString("yyyy-MM-dd") + " close=" + bars[^1].Close
            : "(none)";
        _logger.LogInformation(
            "Engine {Ticker} warmed up with {Bars} bars ({LastBar}), roc={Roc:0.00#%}, signal={Signal:+0;-0;0}, atr={Atr:0.0000}, cash={Cash:0.00}, ready={Ready}",
            Ticker, bars.Count, lastBar, _state.RoC, _state.PendingSignal ?? 0, _state.Atr, _state.Cash, _state.IsWarmedUp);
    }

    private async Task ProcessMinuteAsync(Candle minute, CancellationToken ct)
    {
        DateOnly mskDate = minute.Time.ToMoscowDate();
        OpenPosition? opened = null;
        TradeClosedEvent? closed = null;
        Candle? finalized = null;

        lock (_stateLock)
        {
            bool positionExistedBeforeThisCandle = _state.Position is not null;
            bool newSession = _sessionDate != mskDate;

            if (newSession)
            {
                // A session we joined part-way through has a real close but a
                // fabricated open/high/low; IsComplete tells the engine to keep it
                // out of ATR.
                bool sawPreviousSession = _sessionBar is not null;
                if (_sessionBar is { } previous)
                {
                    _state.FinalizeDay(previous);
                    finalized = previous;
                }

                _sessionDate = mskDate;
                _sessionOpenObserved = sawPreviousSession || IsAtSessionOpen(minute.Time);
                _sessionBar = minute with { IsComplete = _sessionOpenObserved };
                _enteredThisSession = false;
                _exitedThisSession = false;
                _lastMinuteTime = minute.Time;
                _lastMinuteVolume = minute.Volume;
            }
            else if (_sessionBar is { } s)
            {
                // The feed re-sends the SAME minute as it updates, so volume has to
                // be replaced for that minute rather than added again.
                long volumeDelta = _lastMinuteTime == minute.Time
                    ? minute.Volume - _lastMinuteVolume
                    : minute.Volume;
                _sessionBar = s with
                {
                    High = Math.Max(s.High, minute.High),
                    Low = Math.Min(s.Low, minute.Low),
                    Close = minute.Close,
                    Volume = Math.Max(0, s.Volume + volumeDelta),
                };
                _lastMinuteTime = minute.Time;
                _lastMinuteVolume = minute.Volume;
            }

            // Entry at today's open when yesterday's close produced a signal
            // (research: entry at bar i open when signals[i-1] != 0).
            //  * not after an exit in the same session - the position was still open
            //    at that open, so the price is gone by the time the stop/target hits;
            //  * not at all unless this session's open was actually observed - an
            //    engine that joined at 14:00 cannot advise a fill at the 10:00 open.
            if (!positionExistedBeforeThisCandle && !_enteredThisSession
                && !_exitedThisSession && _sessionOpenObserved && _sessionBar is { } bar)
            {
                opened = _state.TryOpen(bar.Open, bar.Time);
                if (opened is not null)
                {
                    _enteredThisSession = true;
                    opened.InstrumentId = InstrumentId;
                    opened.Ticker = Ticker;
                }
            }

            // Intraday SL/TP check (only for positions that existed before this
            // candle, mirroring the research ordering where a just-opened bar is
            // not exited against its own range).
            if (positionExistedBeforeThisCandle)
            {
                closed = _state.CheckStop(minute.High, minute.Low, minute.Time);
                if (closed is not null)
                {
                    _exitedThisSession = true;
                }
            }
        }

        if (finalized is { } bar2)
        {
            lock (_barsLock)
            {
                _dailyBars.Add(bar2);
            }

            if (!bar2.IsComplete)
            {
                _logger.LogWarning(
                    "Session {Date:yyyy-MM-dd} for {Ticker} was only partially observed; "
                    + "its close feeds ROC but its range is kept out of ATR",
                    bar2.Time, Ticker);
            }
        }

        if (opened is not null)
        {
            await _positions.SaveAsync(opened, ct).ConfigureAwait(false);
            var e = new TradeOpenedEvent(
                opened.Ticker, opened.Direction, opened.Units,
                opened.EntryPrice, opened.StopLoss, opened.TakeProfit,
                opened.AtrAtEntry, opened.EntryTime);
            await PublishOpenedAsync(e, ct).ConfigureAwait(false);
        }

        if (closed is not null)
        {
            await PublishClosedAsync(closed, ct).ConfigureAwait(false);
        }

        EngineStateSnapshot snapshot;
        OpenPositionPnl? pnl;
        lock (_stateLock)
        {
            snapshot = _state.GetSnapshot(minute.Close);
            pnl = _state.UnrealizedPnl(minute.Close);
        }

        _metrics.Update(InstrumentId, m =>
        {
            m.LastCandleTime = minute.Time;
            m.LastCandleClose = minute.Close;
            m.LastProcessedBarTime = DateTimeOffset.UtcNow;
            m.BarsProcessed++;
            m.Roc = snapshot.RoC;
            m.Atr = snapshot.Atr;
            m.Cash = snapshot.Cash;
            m.Equity = snapshot.Equity;
            m.PendingSignal = snapshot.PendingSignal;
            ApplyPositionMetrics(m, snapshot, pnl);
        });
    }

    /// <summary>True while a candle still falls inside the entry window after the open.</summary>
    private bool IsAtSessionOpen(DateTimeOffset time)
    {
        TimeSpan msk = time.ToMoscow().TimeOfDay;
        TimeSpan open = MoscowClock.SessionOpen;
        return msk >= open
               && msk < open + TimeSpan.FromMinutes(Math.Max(1, _strategyOptions.EntryWindowMinutes));
    }

    private static void ApplyPositionMetrics(InstrumentMetrics m, EngineStateSnapshot s, OpenPositionPnl? pnl)
    {
        m.PositionState = s.PositionDirection switch
        {
            SignalDirection.Long => "long",
            SignalDirection.Short => "short",
            _ => "flat",
        };
        m.PositionSince = s.EntryTime;
        m.PositionPnl = pnl?.Rub;
        m.PositionPnlPercent = pnl?.Percent;
    }

    private async Task PublishOpenedAsync(TradeOpenedEvent e, CancellationToken ct)
    {
        await _signalLogs.AddAsync(new SignalLogEntry
        {
            InstrumentId = InstrumentId,
            Ticker = e.Ticker,
            Type = SignalType.PositionOpened,
            Direction = e.Direction,
            Price = e.EntryPrice,
            StopLoss = e.StopLoss,
            TakeProfit = e.TakeProfit,
            Timestamp = e.EntryTime,
        }, ct).ConfigureAwait(false);

        try
        {
            await _telegram.SendMessageAsync(_formatter.Opened(e), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram notification failed on open for {Ticker}", e.Ticker);
        }

        _logger.LogInformation(
            "Opened {Direction} {Ticker}: {Units} @ {Entry} at {Time:O}, SL {Sl}, TP {Tp}",
            e.Direction, e.Ticker, e.Units, e.EntryPrice, e.EntryTime, e.StopLoss, e.TakeProfit);
    }

    private async Task PublishClosedAsync(TradeClosedEvent e, CancellationToken ct)
    {
        await _signalLogs.AddAsync(new SignalLogEntry
        {
            InstrumentId = InstrumentId,
            Ticker = e.Ticker,
            Type = SignalType.PositionClosed,
            Direction = e.Direction,
            Price = e.ExitPrice,
            ExitPrice = e.ExitPrice,
            ExitReason = e.Reason,
            PnlPercent = e.ReturnPercent,
            Timestamp = e.ExitTime,
        }, ct).ConfigureAwait(false);

        await _tradeLogs.AddAsync(new TradeLogEntry
        {
            InstrumentId = InstrumentId,
            Ticker = e.Ticker,
            Direction = e.Direction,
            Units = e.Units,
            EntryPrice = e.EntryPrice,
            ExitPrice = e.ExitPrice,
            PnlRub = e.PnlRub,
            ReturnPercent = e.ReturnPercent,
            ExitReason = e.Reason,
            Commission = e.Commission,
            EntryTime = e.EntryTime,
            ExitTime = e.ExitTime,
        }, ct).ConfigureAwait(false);

        await _positions.DeleteAsync(InstrumentId, ct).ConfigureAwait(false);

        try
        {
            await _telegram.SendMessageAsync(_formatter.Closed(e), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram notification failed on close for {Ticker}", e.Ticker);
        }

        _logger.LogInformation(
            "Closed {Direction} {Ticker}: {Units} from {Entry} to {Exit} ({Reason}), pnl={Pnl:0.00}% / {PnlRub:+0;-#;0} RUB",
            e.Direction, e.Ticker, e.Units, e.EntryPrice, e.ExitPrice, e.Reason, e.ReturnPercent, e.PnlRub);
    }
}