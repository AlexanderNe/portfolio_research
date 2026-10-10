using PortfolioTradingSystem.Application.Admin;
using PortfolioTradingSystem.Application.Engine;
using PortfolioTradingSystem.Application.Ports;
using PortfolioTradingSystem.Domain.Models;

namespace PortfolioTradingSystem.Tests;

/// <summary>
/// Unit coverage for the daily equity-curve builder (shared by the portfolio
/// /equity-data endpoint and the per-instrument details-page tab): pool scaling,
/// realized-then-unrealized day semantics, the mark-to-market fallback for an
/// open position and the synthetic "now" point, including the per-instrument
/// subset of live unrealized metrics.
/// </summary>
public sealed class EquityCurveBuilderTests
{
    private static readonly Guid IdA = Guid.NewGuid();
    private static readonly Guid IdB = Guid.NewGuid();

    private static Instrument Instrument(Guid id, string ticker, string uid) => new()
    {
        Id = id,
        Ticker = ticker,
        Name = "Test " + ticker,
        Uid = uid,
    };

    private static Instrument InstA() => Instrument(IdA, "TSTA", "uid-TSTA");
    private static Instrument InstB() => Instrument(IdB, "TSTB", "uid-TSTB");

    private static Task<EquityDataDto> Build(
        IReadOnlyList<Instrument> instruments,
        FakeHistory history,
        FakeTradeLog trades,
        FakePositions positions,
        FakeMetrics metrics,
        DateTimeOffset now)
        => EquityCurveBuilder.BuildAsync(
            instruments,
            new FakeSupervisor(),
            history,
            trades,
            positions,
            metrics,
            TestHelpers.Defaults(),
            now,
            CancellationToken.None);

    [Fact]
    public async Task EmptyHistory_FlatInstrument_SingleSyntheticPointAtNow()
    {
        var instrument = InstA();
        instrument.Uid = null;
        instrument.Figi = null;

        var now = TestHelpers.T0.AddDays(10);
        var dto = await Build(
            new[] { instrument },
            new FakeHistory(),
            new FakeTradeLog(),
            new FakePositions(),
            new FakeMetrics(),
            now);

        Assert.Equal(100_000m, dto.InitialPool);
        Assert.Equal(0m, dto.Realized);
        Assert.Equal(0m, dto.Unrealized);
        Assert.Equal(100_000m, dto.Equity);
        var point = Assert.Single(dto.Points);
        Assert.Equal(now, point.Time);
        Assert.Equal(100_000m, point.Equity);
    }

    [Fact]
    public async Task RealizedTrade_AddsToEquityFromDayAfterExit()
    {
        var history = new FakeHistory
        {
            Bars = new[]
            {
                TestHelpers.Day(0, 100, 100, 100),
                TestHelpers.Day(1, 105, 105, 105),
                TestHelpers.Day(2, 107, 107, 107),
            },
        };
        var trades = new FakeTradeLog
        {
            Trades = new[]
            {
                new TradeLogEntry
                {
                    Ticker = "TSTA", InstrumentId = IdA, Direction = SignalDirection.Long,
                    Units = 10, EntryPrice = 100, ExitPrice = 105, PnlRub = 49.18m,
                    EntryTime = TestHelpers.T0, ExitTime = TestHelpers.T0.AddDays(1),
                },
            },
        };

        var dto = await Build(
            new[] { InstA() },
            history,
            trades,
            new FakePositions(),
            new FakeMetrics(),
            TestHelpers.T0.AddDays(20));

        // day0: entry day marked to market at close 100 -> net -0.80 commission.
        // day1: exit day -> net 49.18 (50 gross minus both commissions).
        // day2: realized 49.18 carries from the day after exit.
        Assert.Equal(4, dto.Points.Count);
        Assert.Equal(99_999.20m, dto.Points[0].Equity, 6);
        Assert.Equal(100_049.18m, dto.Points[1].Equity, 6);
        Assert.Equal(100_049.18m, dto.Points[2].Equity, 6);
        Assert.Equal(100_049.18m, dto.Points[3].Equity, 6);
        Assert.Equal(49.18m, dto.Realized, 6);
        Assert.Equal(0m, dto.Unrealized);
        Assert.Equal(100_049.18m, dto.Equity, 6);
    }

    [Fact]
    public async Task OpenPosition_MarkToMarketDaily_FinalPointCarriesLiveMark()
    {
        var open = new OpenPosition
        {
            InstrumentId = IdA, Ticker = "TSTA", Direction = SignalDirection.Long,
            Units = 10, EntryPrice = 100, EntryTime = TestHelpers.T0,
        };
        var history = new FakeHistory
        {
            Bars = new[]
            {
                TestHelpers.Day(0, 100, 100, 100),
                TestHelpers.Day(1, 102, 102, 102),
                TestHelpers.Day(2, 101, 101, 101),
                TestHelpers.Day(3, 103, 103, 103),
            },
        };
        var metrics = new FakeMetrics
        {
            ById = new()
            {
                [IdA] = new InstrumentMetrics { InstrumentId = IdA, Ticker = "TSTA", PositionPnl = 29.188m },
            },
        };

        var dto = await Build(
            new[] { InstA() },
            history,
            new FakeTradeLog(),
            new FakePositions { ById = new() { [IdA] = open } },
            metrics,
            TestHelpers.T0.AddDays(5));

        // net of 0.04% commissions both sides: (close-100)*10 - (close+100)*10*0.0004.
        Assert.Equal(5, dto.Points.Count);
        Assert.Equal(99_999.20m, dto.Points[0].Equity, 6);
        Assert.Equal(100_019.192m, dto.Points[1].Equity, 6);
        Assert.Equal(100_009.196m, dto.Points[2].Equity, 6);
        Assert.Equal(100_029.188m, dto.Points[3].Equity, 6);
        Assert.Equal(100_029.188m, dto.Points[4].Equity, 6);
        Assert.Equal(29.188m, dto.Unrealized, 6);
        Assert.Equal(100_029.188m, dto.Equity, 6);
    }

    [Fact]
    public async Task Portfolio_SingleInstrumentScalesPoolAndSubsetsLiveUnrealized()
    {
        var history = new FakeHistory
        {
            Bars = new[]
            {
                TestHelpers.Day(0, 100, 100, 100),
                TestHelpers.Day(1, 110, 110, 110),
                TestHelpers.Day(2, 112, 112, 112),
            },
        };
        var trades = new FakeTradeLog
        {
            Trades = new[]
            {
                new TradeLogEntry
                {
                    Ticker = "TSTA", InstrumentId = IdA, Direction = SignalDirection.Long,
                    Units = 10, EntryPrice = 100, ExitPrice = 110, PnlRub = 100m,
                    EntryTime = TestHelpers.T0, ExitTime = TestHelpers.T0.AddDays(1),
                },
            },
        };
        var metrics = new FakeMetrics
        {
            ById = new()
            {
                [IdA] = new InstrumentMetrics { InstrumentId = IdA, Ticker = "TSTA", PositionPnl = 500m },
                [IdB] = new InstrumentMetrics { InstrumentId = IdB, Ticker = "TSTB", PositionPnl = 300m },
            },
        };
        var now = TestHelpers.T0.AddDays(30);

        var portfolio = await Build(
            new[] { InstA(), InstB() }, history, trades, new FakePositions(), metrics, now);
        Assert.Equal(200_000m, portfolio.InitialPool);
        Assert.Equal(100m, portfolio.Realized);
        Assert.Equal(800m, portfolio.Unrealized);
        Assert.Equal(200_900m, portfolio.Equity);
        Assert.Equal(200_900m, portfolio.Points[^1].Equity);

        var single = await Build(
            new[] { InstA() }, history, trades, new FakePositions(), metrics, now);
        Assert.Equal(100_000m, single.InitialPool);
        Assert.Equal(100m, single.Realized);
        Assert.Equal(500m, single.Unrealized);
        Assert.Equal(100_600m, single.Equity);
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

        public Task<IReadOnlyList<Candle>> GetDailyBarsAsync(string instrumentId, int maxBars, CancellationToken ct)
            => Task.FromResult(Bars);

        public Task<Candle?> GetCurrentDayBarAsync(string instrumentId, CancellationToken ct)
            => Task.FromResult<Candle?>(null);
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
        public Dictionary<Guid, OpenPosition> ById { get; init; } = new();

        public Task<OpenPosition?> GetOpenAsync(Guid instrumentId, CancellationToken ct)
            => Task.FromResult(ById.TryGetValue(instrumentId, out var open) ? open : null);

        public Task SaveAsync(OpenPosition position, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid instrumentId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeMetrics : IMetricsStore
    {
        public Dictionary<Guid, InstrumentMetrics> ById { get; init; } = new();

        public InstrumentMetrics? Get(Guid instrumentId)
            => ById.TryGetValue(instrumentId, out var m) ? m : null;

        public IReadOnlyList<InstrumentMetrics> GetAll() => ById.Values.ToList();

        public void Initialize(Guid instrumentId, string ticker) { }
        public void Remove(Guid instrumentId) { }
        public void RemoveAll() { }
        public void Update(Guid instrumentId, Action<InstrumentMetrics> mutate) { }
    }
}