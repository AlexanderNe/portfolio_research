using PortfolioTradingSystem.Application.Admin;
using PortfolioTradingSystem.Application.Engine;
using PortfolioTradingSystem.Application.Ports;
using PortfolioTradingSystem.Domain.Models;

namespace PortfolioTradingSystem.Tests;

/// <summary>
/// Unit coverage for the per-instrument details payload: PnL aggregation, the
/// mark-to-market fallback, pending-signal passthrough, the recent-signals limit
/// and the signal-day anchoring (placeholder candles / in-progress day bar).
/// </summary>
public sealed class InstrumentDetailsBuilderTests
{
    private static readonly Guid Id = Guid.NewGuid();

    private static Instrument Instrument() => new()
    {
        Id = Id,
        Ticker = "TST",
        Name = "Test Bank",
        Uid = "uid-TST",
    };

    private static Task<InstrumentDetailsDto> Build(
        Instrument instrument,
        FakeHistory history,
        FakeTradeLog trades,
        FakePositions positions,
        FakeMetrics metrics,
        FakeSignals signals,
        DateTimeOffset? now = null)
        => InstrumentDetailsBuilder.BuildAsync(
            instrument,
            new FakeSupervisor(),
            history,
            trades,
            positions,
            metrics,
            signals,
            TestHelpers.Defaults(),
            now ?? TestHelpers.T0.AddDays(10),
            CancellationToken.None);

    [Fact]
    public async Task FlatInstrument_ReturnsZeroPnlAndNoSignals()
    {
        var dto = await Build(
            Instrument(),
            new FakeHistory { Bars = new[] { TestHelpers.Day(0, 100, 100, 100), TestHelpers.Day(1, 101, 101, 101) } },
            new FakeTradeLog(),
            new FakePositions(),
            new FakeMetrics(),
            new FakeSignals());

        Assert.Equal("TST", dto.Ticker);
        Assert.Equal("Test Bank", dto.Name);
        Assert.Equal(2, dto.Candles.Count);
        Assert.Empty(dto.Trades);
        Assert.Null(dto.OpenPosition);
        Assert.Equal(0m, dto.RealizedPnlRub);
        Assert.Null(dto.UnrealizedPnlRub);
        Assert.Null(dto.UnrealizedPnlPercent);
        Assert.Equal(0m, dto.TotalPnlRub);
        Assert.Equal(0m, dto.TotalPnlPercent);
        Assert.Null(dto.PendingSignal);
        Assert.Empty(dto.Signals);
    }

    [Fact]
    public async Task NoHistoryIdAndNoEngine_ReturnsEmptyCandles()
    {
        var instrument = Instrument();
        instrument.Uid = null;
        instrument.Figi = null;

        var dto = await Build(
            instrument,
            new FakeHistory(),
            new FakeTradeLog(),
            new FakePositions(),
            new FakeMetrics(),
            new FakeSignals());

        Assert.Empty(dto.Candles);
        Assert.Equal(0m, dto.RealizedPnlRub);
    }

    [Fact]
    public async Task RealizedOnly_SumsPnlAndStatsVsInitialCapital()
    {
        var trades = new FakeTradeLog
        {
            Trades = new[]
            {
                new TradeLogEntry
                {
                    Ticker = "TST", InstrumentId = Id, Direction = SignalDirection.Long,
                    Units = 10, EntryPrice = 100, ExitPrice = 110, PnlRub = 100m,
                    ReturnPercent = 10m, ExitReason = ExitReason.TakeProfit,
                    EntryTime = TestHelpers.T0, ExitTime = TestHelpers.T0.AddDays(1),
                },
                new TradeLogEntry
                {
                    Ticker = "TST", InstrumentId = Id, Direction = SignalDirection.Short,
                    Units = 5, EntryPrice = 200, ExitPrice = 198, PnlRub = -25m,
                    ReturnPercent = -1m, ExitReason = ExitReason.StopLoss,
                    EntryTime = TestHelpers.T0, ExitTime = TestHelpers.T0.AddDays(1),
                },
            },
        };

        var dto = await Build(
            Instrument(),
            new FakeHistory { Bars = new[] { TestHelpers.Day(0, 100, 100, 100), TestHelpers.Day(1, 101, 101, 101) } },
            trades,
            new FakePositions(),
            new FakeMetrics(),
            new FakeSignals());

        Assert.Equal(75m, dto.RealizedPnlRub);
        Assert.Null(dto.UnrealizedPnlRub);
        Assert.Equal(75m, dto.TotalPnlRub);
        Assert.Equal(0.075m, dto.TotalPnlPercent, 10);
        Assert.Equal(2, dto.Trades.Count);
        Assert.Equal("Long", dto.Trades[0].Direction);
        Assert.Equal("TakeProfit", dto.Trades[0].Reason);
        Assert.Equal(10m, dto.Trades[0].PnlPercent);
        Assert.Equal("Short", dto.Trades[1].Direction);
    }

    [Fact]
    public async Task OpenPosition_WithLiveMetrics_UsesLiveMarkAndPendingSignal()
    {
        var open = new OpenPosition
        {
            InstrumentId = Id, Ticker = "TST", Direction = SignalDirection.Long,
            Units = 10, EntryPrice = 100, StopLoss = 95, TakeProfit = 115,
            EntryTime = TestHelpers.T0,
        };
        var metrics = new FakeMetrics
        {
            Metrics = new InstrumentMetrics
            {
                InstrumentId = Id,
                Ticker = "TST",
                PendingSignal = -1,
                PositionPnl = 500m,
                PositionPnlPercent = 2.5m,
            },
        };

        var dto = await Build(
            Instrument(),
            new FakeHistory { Bars = new[] { TestHelpers.Day(0, 105, 105, 105) } },
            new FakeTradeLog(),
            new FakePositions { Open = open },
            metrics,
            new FakeSignals());

        Assert.Equal(500m, dto.UnrealizedPnlRub);
        Assert.Equal(2.5m, dto.UnrealizedPnlPercent);
        Assert.Equal(500m, dto.TotalPnlRub);
        Assert.Equal(0.5m, dto.TotalPnlPercent, 10);
        Assert.Equal(-1, dto.PendingSignal);
        Assert.NotNull(dto.OpenPosition);
        Assert.Equal("Long", dto.OpenPosition.Direction);
        Assert.Equal(100m, dto.OpenPosition.EntryPrice);
        Assert.Equal(95m, dto.OpenPosition.StopLoss);
        Assert.Equal(115m, dto.OpenPosition.TakeProfit);
        Assert.Equal(10, dto.OpenPosition.Units);
    }

    [Fact]
    public async Task OpenPosition_WithoutLiveMetrics_MarkToMarketOnLastClose()
    {
        var open = new OpenPosition
        {
            InstrumentId = Id, Ticker = "TST", Direction = SignalDirection.Long,
            Units = 10, EntryPrice = 100, StopLoss = 95, TakeProfit = 115,
            EntryTime = TestHelpers.T0,
        };

        var dto = await Build(
            Instrument(),
            new FakeHistory { Bars = new[] { TestHelpers.Day(0, 105, 105, 105) } },
            new FakeTradeLog(),
            new FakePositions { Open = open },
            new FakeMetrics(),
            new FakeSignals());

        // gross 5*10*1 = 50; commissions 0.04% both sides: 105*10 + 100*10 * 0.0004.
        Assert.NotNull(dto.UnrealizedPnlRub);
        Assert.NotNull(dto.UnrealizedPnlPercent);
        Assert.Equal(49.18m, dto.UnrealizedPnlRub.Value, 10);
        Assert.Equal(4.918m, dto.UnrealizedPnlPercent.Value, 10);
        Assert.Equal(49.18m, dto.TotalPnlRub, 10);
        Assert.Equal(0.04918m, dto.TotalPnlPercent, 10);
    }

    [Fact]
    public async Task RecentSignals_RequestsExactlyFiftyEntries()
    {
        var signals = new FakeSignals
        {
            Signals = new[]
            {
                new SignalLogEntry { Ticker = "TST", Timestamp = TestHelpers.T0 },
                new SignalLogEntry { Ticker = "TST", Timestamp = TestHelpers.T0.AddDays(1) },
                new SignalLogEntry { Ticker = "TST", Timestamp = TestHelpers.T0.AddDays(2) },
            },
        };

        var dto = await Build(
            Instrument(),
            new FakeHistory { Bars = new[] { TestHelpers.Day(0, 100, 100, 100) } },
            new FakeTradeLog(),
            new FakePositions(),
            new FakeMetrics(),
            signals);

        Assert.Equal(3, dto.Signals.Count);
        Assert.Equal(InstrumentDetailsBuilder.RecentSignalsLimit, signals.LastLimit);
    }

    [Fact]
    public async Task TradedDaysWithoutPriceData_BecomePlaceholderCandles()
    {
        var trades = new FakeTradeLog
        {
            Trades = new[]
            {
                new TradeLogEntry
                {
                    Ticker = "TST", InstrumentId = Id, Direction = SignalDirection.Long,
                    EntryPrice = 100, ExitPrice = 110, PnlRub = 50m,
                    EntryTime = TestHelpers.T0.AddDays(5), ExitTime = TestHelpers.T0.AddDays(5),
                },
            },
        };
        var open = new OpenPosition
        {
            InstrumentId = Id, Ticker = "TST", Direction = SignalDirection.Long,
            EntryPrice = 100, EntryTime = TestHelpers.T0.AddDays(3),
        };

        var dto = await Build(
            Instrument(),
            new FakeHistory { Bars = new[] { TestHelpers.Day(0, 100, 100, 100), TestHelpers.Day(1, 101, 101, 101) } },
            trades,
            new FakePositions { Open = open },
            new FakeMetrics(),
            new FakeSignals());

        Assert.Equal(4, dto.Candles.Count);
        Assert.True(dto.Candles[0] is { IsPlaceholder: false } && dto.Candles[1] is { IsPlaceholder: false });
        Assert.True(dto.Candles[2].IsPlaceholder);
        Assert.True(dto.Candles[3].IsPlaceholder);
        Assert.Equal(101m, dto.Candles[2].Close);
        Assert.Equal(101m, dto.Candles[3].Close);
        Assert.Equal(DateOnly.FromDateTime(TestHelpers.T0.AddDays(3).Date), MoscowClock.ToMoscowDate(dto.Candles[2].Time));
        Assert.Equal(DateOnly.FromDateTime(TestHelpers.T0.AddDays(5).Date), MoscowClock.ToMoscowDate(dto.Candles[3].Time));
        for (int i = 1; i < dto.Candles.Count; i++)
        {
            Assert.True(dto.Candles[i - 1].Time < dto.Candles[i].Time, "candles must be time-ascending");
        }
    }

    [Fact]
    public async Task InProgressDayBar_AppendedWhenTodayCandleMissing()
    {
        var history = new FakeHistory
        {
            Bars = new[] { TestHelpers.Day(0, 100, 100, 100) },
            CurrentDayBar = new Candle(TestHelpers.T0.AddDays(2).AddHours(2), 111, 111, 111, 111, 1000, IsComplete: false),
        };

        var dto = await Build(
            Instrument(),
            history,
            new FakeTradeLog(),
            new FakePositions(),
            new FakeMetrics(),
            new FakeSignals(),
            now: TestHelpers.T0.AddDays(2));

        Assert.Equal(2, dto.Candles.Count);
        Assert.Equal(111m, dto.Candles[^1].Close);
        Assert.False(dto.Candles[^1].IsPlaceholder);
        Assert.Equal("uid-TST", Assert.Single(history.CurrentDayBarIdRequests));
    }

    private sealed class FakeSupervisor : IEngineSupervisor
    {
        public IReadOnlyList<InstrumentEngine> Engines { get; } = Array.Empty<InstrumentEngine>();
        public bool IsGloballyRunning { get; } = false;

        public InstrumentEngine? GetEngine(Guid instrumentId) => null;

        public Task SyncInstrumentsAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAllAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ResumeAllAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ResetAllAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeHistory : IHistoricDailyBarsProvider
    {
        public IReadOnlyList<Candle> Bars { get; init; } = Array.Empty<Candle>();
        public Candle? CurrentDayBar { get; init; }
        public List<string> CurrentDayBarIdRequests { get; } = new();

        public Task<IReadOnlyList<Candle>> GetDailyBarsAsync(string instrumentId, int maxBars, CancellationToken ct)
            => Task.FromResult(Bars);

        public Task<Candle?> GetCurrentDayBarAsync(string instrumentId, CancellationToken ct)
        {
            CurrentDayBarIdRequests.Add(instrumentId);
            return Task.FromResult(CurrentDayBar);
        }
    }

    private sealed class FakeTradeLog : ITradeLogRepository
    {
        public IReadOnlyList<TradeLogEntry> Trades { get; init; } = Array.Empty<TradeLogEntry>();

        public Task AddAsync(TradeLogEntry entry, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<TradeLogEntry>> GetByInstrumentAsync(Guid instrumentId, int limit, CancellationToken ct)
            => Task.FromResult(Trades);

        public Task<IReadOnlyList<TradeLogEntry>> GetAllByInstrumentAsync(Guid instrumentId, CancellationToken ct)
            => Task.FromResult(Trades);

        public Task<decimal> SumRealizedPnlAsync(Guid instrumentId, CancellationToken ct)
            => Task.FromResult(Trades.Sum(t => t.PnlRub));

        public Task<decimal> SumAllRealizedPnlAsync(CancellationToken ct)
            => Task.FromResult(Trades.Sum(t => t.PnlRub));

        public Task<RealizedPnlByDirection> SumAllRealizedPnlByDirectionAsync(CancellationToken ct)
            => Task.FromResult(new RealizedPnlByDirection(0m, 0m));

        public Task<IReadOnlyList<TradeLogEntry>> GetAllAsync(CancellationToken ct)
            => Task.FromResult(Trades);
    }

    private sealed class FakePositions : IPositionRepository
    {
        public OpenPosition? Open { get; init; }

        public Task<OpenPosition?> GetOpenAsync(Guid instrumentId, CancellationToken ct)
            => Task.FromResult(Open);

        public Task SaveAsync(OpenPosition position, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid instrumentId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeMetrics : IMetricsStore
    {
        public InstrumentMetrics? Metrics { get; init; }

        public void Initialize(Guid instrumentId, string ticker) { }
        public void Remove(Guid instrumentId) { }
        public void RemoveAll() { }

        public InstrumentMetrics? Get(Guid instrumentId) => Metrics;

        public IReadOnlyList<InstrumentMetrics> GetAll()
            => Metrics is null ? Array.Empty<InstrumentMetrics>() : new[] { Metrics };

        public void Update(Guid instrumentId, Action<InstrumentMetrics> mutate) { }
    }

    private sealed class FakeSignals : ISignalLogRepository
    {
        public IReadOnlyList<SignalLogEntry> Signals { get; init; } = Array.Empty<SignalLogEntry>();
        public int? LastLimit { get; private set; }

        public Task AddAsync(SignalLogEntry entry, CancellationToken ct) => Task.CompletedTask;

        public Task<SignalLogEntry?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult<SignalLogEntry?>(null);

        public Task<SignalLogEntry?> GetLatestOpenedAsync(Guid instrumentId, CancellationToken ct)
            => Task.FromResult<SignalLogEntry?>(null);

        public Task<IReadOnlyList<SignalLogEntry>> GetByInstrumentAsync(Guid instrumentId, int limit, CancellationToken ct)
        {
            LastLimit = limit;
            return Task.FromResult(Signals);
        }

        public Task<SignalLogPage> GetPageAsync(string? ticker, int page, int pageSize, CancellationToken ct)
            => Task.FromResult(new SignalLogPage(0, page, pageSize, Array.Empty<SignalLogEntry>()));

        public Task<IReadOnlyList<string>> GetTickersAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}