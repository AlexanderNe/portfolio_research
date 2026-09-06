using System.Runtime.CompilerServices;
using Grpc.Core;
using PortfolioTradingSystem.Application.Ports;
using Candle = PortfolioTradingSystem.Domain.Models.Candle;
using PortfolioTradingSystem.Infrastructure.Tinkoff;
using Tinkoff.InvestApi.V1;

namespace PortfolioTradingSystem.Infrastructure.MarketData;

/// <summary>
/// Real-time 1-minute candle feed. Opens one server-side gRPC stream per call
/// (one live caller/engine at a time per instrument). Reconnection is handled by
/// the caller (InstrumentEngine) with exponential backoff.
/// </summary>
public sealed class TinkoffMarketDataGateway : IMarketDataGateway
{
    private readonly TinkoffConnection _connection;

    public TinkoffMarketDataGateway(TinkoffConnection connection)
    {
        _connection = connection;
    }

    public IAsyncEnumerable<Candle> StreamOneMinuteCandlesAsync(string instrumentId, CancellationToken ct)
    {
        var request = new MarketDataServerSideStreamRequest
        {
            SubscribeCandlesRequest = new SubscribeCandlesRequest
            {
                SubscriptionAction = SubscriptionAction.Subscribe,
                Instruments =
                {
                    new CandleInstrument { InstrumentId = instrumentId, Interval = SubscriptionInterval.OneMinute },
                },
            },
            PingSettings = new PingDelaySettings { PingDelayMs = 30000 },
        };

        var call = _connection.MarketDataStream.MarketDataServerSideStream(
            request,
            new CallOptions(headers: _connection.Metadata, cancellationToken: ct));
        return Enumerate(call, ct);
    }

    private static async IAsyncEnumerable<Candle> Enumerate(
        AsyncServerStreamingCall<MarketDataResponse> call,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            while (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
            {
                var message = call.ResponseStream.Current;
                if (message.Candle is not null)
                {
                    yield return TinkoffMappers.ToCandle(message.Candle);
                }
            }
        }
        finally
        {
            call.Dispose();
        }
    }
}