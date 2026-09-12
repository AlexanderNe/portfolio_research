using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PortfolioTradingSystem.Application.Configuration;
using PortfolioTradingSystem.Application.Engine;
using PortfolioTradingSystem.Application.Ports;
using PortfolioTradingSystem.Domain.Models;
using PortfolioTradingSystem.Domain.Strategy;

namespace PortfolioTradingSystem.Tests;

/// <summary>In-memory doubles so the engine can be driven without a broker or a database.</summary>
internal sealed class FakeMarketData : IMarketDataGateway
{
    private readonly Channel<Candle> _channel = Channel.CreateUnbounded<Candle>();

    public void Push(Candle candle) => _channel.Writer.TryWrite(candle);

    public async IAsyncEnumerable<Candle> SubscribeAsync(
        string instrumentId, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var candle in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return candle;
        }
    }
}

internal sealed class FakeHistory : IHistoricDailyBarsProvider
{
    private readonly IReadOnlyList<Candle> _bars;

    public FakeHistory(IReadOnlyList<Candle> bars) => _bars = bars;

    public Task<IReadOnlyList<Candle>> GetDailyBarsAsync(string instrumentId, int maxBars, CancellationToken ct) =>
        Task.FromResult(_bars);
}

internal sealed class FakeTelegram : ITelegramGateway
{
    public List<string> Messages { get; } = new();

    public Task SendMessageAsync(string text, CancellationToken ct)
    {
        lock (Messages)
        {
            Messages.Add(text);
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakePositions : IPositionRepository
{
    public OpenPosition? Current { get; set; }

    public Task<OpenPosition?> GetOpenAsync(Guid instrumentId, CancellationToken ct) => Task.FromResult(Current);

    public Task SaveAsync(OpenPosition position, CancellationToken ct)
    {
        Current = position;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid instrumentId, CancellationToken ct)
    {
        Current = null;
        return Task.CompletedTask;
    }
}

internal sealed class FakeTradeLogs : ITradeLogRepository
{
    public List<TradeLogEntry> Entries { get; } = new();

    public decimal Realized { get; set; }

    public Task AddAsync(TradeLogEntry entry, CancellationToken ct)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TradeLogEntry>> GetByInstrumentAsync(Guid instrumentId, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TradeLogEntry>>(Entries);

    public Task<IReadOnlyList<TradeLogEntry>> GetAllByInstrumentAsync(Guid instrumentId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TradeLogEntry>>(Entries);

    public Task<decimal> SumRealizedPnlAsync(Guid instrumentId, CancellationToken ct) => Task.FromResult(Realized);

    public Task<decimal> SumAllRealizedPnlAsync(CancellationToken ct) => Task.FromResult(Realized);

    public Task<IReadOnlyList<TradeLogEntry>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TradeLogEntry>>(Entries);
}

internal sealed class FakeJournal : ITradeJournal
{
    private readonly FakePositions _positions;

    public FakeJournal(FakePositions positions) => _positions = positions;

    public List<OpenPosition> Opens { get; } = new();

    public List<TradeLogEntry> Closes { get; } = new();

    public Task RecordOpenAsync(OpenPosition position, SignalLogEntry signal, CancellationToken ct)
    {
        Opens.Add(position);
        _positions.Current = position;
        return Task.CompletedTask;
    }

    public Task RecordCloseAsync(Guid instrumentId, TradeLogEntry trade, SignalLogEntry signal, CancellationToken ct)
    {
        Closes.Add(trade);
        _positions.Current = null;
        return Task.CompletedTask;
    }
}

/// <summary>Builds an <see cref="InstrumentEngine"/> wired to the fakes above.</summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    public EngineHarness(IReadOnlyList<Candle> warmUpBars, StrategyOptions? options = null, int lotSize = 1)
    {
        Options = options ?? TestHelpers.Defaults();
        Instrument = new Instrument { Ticker = "TST", Uid = "uid-tst", LotSize = lotSize };
        MarketData = new FakeMarketData();
        Positions = new FakePositions();
        TradeLogs = new FakeTradeLogs();
        Journal = new FakeJournal(Positions);
        Telegram = new FakeTelegram();
        Metrics = new InMemoryMetricsStore();

        Engine = new InstrumentEngine(
            Instrument,
            Options,
            MarketData,
            new FakeHistory(warmUpBars),
            Telegram,
            Positions,
            TradeLogs,
            Journal,
            Metrics,
            new TelegramMessageFormatter(),
            Microsoft.Extensions.Options.Options.Create(new EngineOptions { ReconnectDelaySeconds = 1, ReconnectMaxDelaySeconds = 1 }),
            Microsoft.Extensions.Options.Options.Create(new TinkoffOptions { HistoricMaxBars = 700 }),
            NullLogger<InstrumentEngine>.Instance);
    }

    public StrategyOptions Options { get; }

    public Instrument Instrument { get; }

    public FakeMarketData MarketData { get; }

    public FakePositions Positions { get; }

    public FakeTradeLogs TradeLogs { get; }

    public FakeJournal Journal { get; }

    public FakeTelegram Telegram { get; }

    public InMemoryMetricsStore Metrics { get; }

    public InstrumentEngine Engine { get; }

    /// <summary>Push candles and wait until the engine has processed all of them.</summary>
    public async Task FeedAsync(params Candle[] candles)
    {
        long processed = Metrics.Get(Instrument.Id)?.BarsProcessed ?? 0;
        foreach (var candle in candles)
        {
            MarketData.Push(candle);
        }

        long target = processed + candles.Length;
        for (int i = 0; i < 500; i++)
        {
            if ((Metrics.Get(Instrument.Id)?.BarsProcessed ?? 0) >= target)
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"engine processed {Metrics.Get(Instrument.Id)?.BarsProcessed} of {target} candles");
    }

    public async Task StartAsync()
    {
        Engine.Start();
        for (int i = 0; i < 500; i++)
        {
            if (Metrics.Get(Instrument.Id)?.WarmupDone == true)
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException("engine did not finish warm-up");
    }

    public ValueTask DisposeAsync() => Engine.DisposeAsync();
}
