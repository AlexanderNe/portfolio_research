using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioTradingSystem.Application.Configuration;
using PortfolioTradingSystem.Application.Ports;
using PortfolioTradingSystem.Domain.Models;
using PortfolioTradingSystem.Infrastructure.Tinkoff;
using Tinkoff.InvestApi.V1;
using Candle = PortfolioTradingSystem.Domain.Models.Candle;

namespace PortfolioTradingSystem.Infrastructure.MarketData;

/// <summary>
/// Real-time 1-minute candle feed multiplexed over ONE server-side market-data stream.
/// T-Bank limits the number of simultaneously open quotes streams (32) but allows up
/// to 300 subscriptions per stream, so a stream-per-engine design (one per instrument)
/// trips error 80001 "Limit of open streams exceeded" as soon as several engines run.
/// Here every instrument subscribes on the same connection; the stream is reopened with
/// the full subscription set when the set changes. Reconnects are transparent to callers:
/// per-instrument channels stay open across stream restarts.
/// </summary>
public sealed class TinkoffMarketDataMultiplexer : IMarketDataGateway, IAsyncDisposable
{
    private readonly TinkoffConnection _connection;
    private readonly ILogger<TinkoffMarketDataMultiplexer> _logger;
    private readonly TimeSpan _reconnectStart;
    private readonly TimeSpan _reconnectMax;
    private readonly object _gate = new();
    private readonly Dictionary<string, Channel<Candle>> _subscriptions = new();
    private CancellationTokenSource? _workerCts;
    private CancellationTokenSource? _streamCts;
    private Task? _worker;
    private bool _restartRequested;

    public TinkoffMarketDataMultiplexer(
        TinkoffConnection connection,
        IOptions<EngineOptions> engineOptions,
        ILogger<TinkoffMarketDataMultiplexer> logger)
    {
        _connection = connection;
        _logger = logger;
        _reconnectStart = TimeSpan.FromSeconds(engineOptions.Value.ReconnectDelaySeconds);
        _reconnectMax = TimeSpan.FromSeconds(engineOptions.Value.ReconnectMaxDelaySeconds);
    }

    public IAsyncEnumerable<Candle> SubscribeAsync(string instrumentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(instrumentId))
        {
            throw new ArgumentException("Instrument id is required.", nameof(instrumentId));
        }

        Channel<Candle> channel;
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(instrumentId, out channel!))
            {
                channel = Channel.CreateUnbounded<Candle>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true,
                });
                _subscriptions[instrumentId] = channel;
            }
        }

        RequestRestart();
        EnsureWorkerRunning();
        return ReadAsync(instrumentId, channel, ct);
    }

    private async IAsyncEnumerable<Candle> ReadAsync(
        string instrumentId,
        Channel<Candle> channel,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var registration = ct.Register(() => Unsubscribe(instrumentId));
        try
        {
            await foreach (var candle in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return candle;
            }
        }
        finally
        {
            Unsubscribe(instrumentId);
            registration.Dispose();
        }
    }

    private void Unsubscribe(string instrumentId)
    {
        Channel<Candle>? channel;
        lock (_gate)
        {
            if (!_subscriptions.Remove(instrumentId, out channel))
            {
                return;
            }
        }

        channel!.Writer.TryComplete();
        RequestRestart();
    }

    /// <summary>
    /// Flag the subscription set as changed AND break the current stream read.
    /// Without the cancellation the flag was only ever noticed when the next
    /// message arrived, so an instrument resumed while the market was quiet could
    /// wait indefinitely for its subscription.
    /// </summary>
    private void RequestRestart()
    {
        Volatile.Write(ref _restartRequested, true);
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _streamCts;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void EnsureWorkerRunning()
    {
        // Engines subscribe concurrently: without this lock two of them could both
        // see "no worker" and open two streams, doubling every candle and violating
        // the SingleWriter contract of the per-instrument channels.
        lock (_gate)
        {
            if (_worker is not null && !_worker.IsCompleted)
            {
                return;
            }

            _workerCts?.Dispose();
            _workerCts = new CancellationTokenSource();
            var ct = _workerCts.Token;
            _worker = Task.Run(() => RunWorkerAsync(ct), ct);
        }
    }

    private static bool IsCancellation(Exception ex) =>
        ex is OperationCanceledException
        || (ex is RpcException rpc && rpc.StatusCode == StatusCode.Cancelled);

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        TimeSpan delay = _reconnectStart;
        while (!ct.IsCancellationRequested)
        {
            List<string> ids;
            lock (_gate)
            {
                ids = _subscriptions.Keys.ToList();
                if (ids.Count == 0)
                {
                    _logger.LogDebug("Market data multiplexer has no subscriptions; exiting");
                    return;
                }
            }

            // Membership changed while we were disconnected from the old stream:
            // reopen instead of opening with a stale subscription set.
            if (Volatile.Read(ref _restartRequested))
            {
                Volatile.Write(ref _restartRequested, false);
                await Task.Delay(150, ct).ConfigureAwait(false);
                continue;
            }

            var request = BuildRequest(ids);
            _logger.LogDebug("Opening shared market data stream for {Count} instruments", ids.Count);
            var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_gate)
            {
                _streamCts = streamCts;
            }

            AsyncServerStreamingCall<MarketDataResponse> call = _connection.MarketDataStream.MarketDataServerSideStream(
                request,
                new CallOptions(headers: _connection.Metadata, cancellationToken: streamCts.Token));

            bool reopenedForChange = false;
            try
            {
                await foreach (var message in call.ResponseStream.ReadAllAsync(streamCts.Token).ConfigureAwait(false))
                {
                    if (Volatile.Read(ref _restartRequested))
                    {
                        Volatile.Write(ref _restartRequested, false);
                        reopenedForChange = true;
                        _logger.LogDebug("Subscription set changed; reopening shared stream");
                        break;
                    }

                    if (message.SubscribeCandlesResponse is { } subscribed)
                    {
                        foreach (var status in subscribed.CandlesSubscriptions)
                        {
                            if (status.SubscriptionStatus != SubscriptionStatus.Success)
                            {
                                _logger.LogError(
                                    "Candle subscription rejected for {InstrumentId}: {Status}",
                                    status.InstrumentUid, status.SubscriptionStatus);
                            }
                        }
                    }

                    if (message.Candle is not null)
                    {
                        Channel<Candle>? target;
                        lock (_gate)
                        {
                            // Responses identify candles by InstrumentUid; fall back to FIGI
                            // for instruments subscribed before a resolution set a UID.
                            _subscriptions.TryGetValue(message.Candle.InstrumentUid, out target);
                            if (target is null && !string.IsNullOrEmpty(message.Candle.Figi))
                            {
                                _subscriptions.TryGetValue(message.Candle.Figi, out target);
                            }
                        }

                        target?.Writer.TryWrite(TinkoffMappers.ToCandle(message.Candle));
                    }
                }

                if (!ct.IsCancellationRequested)
                {
                    if (reopenedForChange)
                    {
                        await Task.Delay(150, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Genuine end of stream (server-side EOF) with an unchanged set.
                        _logger.LogWarning("Shared market data stream ended; reconnecting in {Delay}s", delay.TotalSeconds);
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _reconnectMax.TotalMilliseconds));
                    }
                }
            }
            catch (Exception ex) when (IsCancellation(ex) && ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (IsCancellation(ex))
            {
                // The subscription set changed and cancelled the read: reopen at once.
                Volatile.Write(ref _restartRequested, false);
                delay = _reconnectStart;
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shared market data stream failed; reconnecting in {Delay}s", delay.TotalSeconds);
                Volatile.Write(ref _restartRequested, false);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _reconnectMax.TotalMilliseconds));
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_streamCts, streamCts))
                    {
                        _streamCts = null;
                    }
                }

                streamCts.Dispose();
                call.Dispose();
            }
        }
    }

    private static MarketDataServerSideStreamRequest BuildRequest(IReadOnlyList<string> instrumentIds)
    {
        var request = new MarketDataServerSideStreamRequest
        {
            SubscribeCandlesRequest = new SubscribeCandlesRequest
            {
                SubscriptionAction = SubscriptionAction.Subscribe,
                Instruments =
                {
                    instrumentIds.Select(id => new CandleInstrument
                    {
                        InstrumentId = id,
                        Interval = SubscriptionInterval.OneMinute,
                    }),
                },
            },
            PingSettings = new PingDelaySettings { PingDelayMs = 30000 },
        };
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _streamCts?.Cancel();
        }

        _workerCts?.Cancel();
        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Market data multiplexer worker shutdown");
            }
        }

        _workerCts?.Dispose();
        lock (_gate)
        {
            foreach (var channel in _subscriptions.Values)
            {
                channel.Writer.TryComplete();
            }

            _subscriptions.Clear();
        }
    }
}