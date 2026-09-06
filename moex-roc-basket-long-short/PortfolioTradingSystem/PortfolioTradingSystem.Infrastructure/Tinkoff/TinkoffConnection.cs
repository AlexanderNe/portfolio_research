using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioTradingSystem.Application.Configuration;
using Tinkoff.InvestApi.V1;

namespace PortfolioTradingSystem.Infrastructure.Tinkoff;

/// <summary>
/// Shared gRPC channel + authenticated clients for the T-Bank Invest API.
/// All real-time streams multiplex over the single HTTP/2 channel.
/// </summary>
public sealed class TinkoffConnection : IDisposable
{
    public TinkoffConnection(IOptions<TinkoffOptions> options, ILogger<TinkoffConnection> logger)
    {
        var o = options.Value;
        string url = o.SandboxMode
            ? "https://sandbox-invest-public-api.tbank.ru:443"
            : (string.IsNullOrWhiteSpace(o.ApiUrl) ? "https://invest-public-api.tbank.ru:443" : o.ApiUrl);

        logger.LogDebug(
            "Tinkoff channel created: url={Url} sandbox={Sandbox} tokenConfigured={TokenConfigured}",
            url, o.SandboxMode, !string.IsNullOrWhiteSpace(o.AccessToken));
        Channel = GrpcChannel.ForAddress(url, new GrpcChannelOptions { MaxReceiveMessageSize = null });
        Metadata = new Metadata { { "Authorization", "Bearer " + o.AccessToken } };
        MarketData = new MarketDataService.MarketDataServiceClient(Channel);
        MarketDataStream = new MarketDataStreamService.MarketDataStreamServiceClient(Channel);
        Instruments = new InstrumentsService.InstrumentsServiceClient(Channel);
    }

    public GrpcChannel Channel { get; }

    public Metadata Metadata { get; }

    public MarketDataService.MarketDataServiceClient MarketData { get; }

    public MarketDataStreamService.MarketDataStreamServiceClient MarketDataStream { get; }

    public InstrumentsService.InstrumentsServiceClient Instruments { get; }

    public void Dispose() => Channel.Dispose();
}