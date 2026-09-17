using Microsoft.EntityFrameworkCore;
using PortfolioTradingSystem.Application.Ports;
using PortfolioTradingSystem.Domain.Enums;
using PortfolioTradingSystem.Domain.Models;

namespace PortfolioTradingSystem.Infrastructure.Persistence;

/// <summary>
/// EF Core repositories. Each operation creates its own short-lived context via
/// <see cref="IDbContextFactory{AppDbContext}"/>, so the repositories are safe
/// to share across engine tasks and HTTP requests.
/// </summary>
public sealed class EfInstrumentRepository : IInstrumentRepository
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public EfInstrumentRepository(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<Instrument>> GetAllAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Instruments.AsNoTracking().OrderBy(x => x.Ticker).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Instrument>> GetActiveAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Instruments.AsNoTracking()
            .Where(x => x.ProcessingStatus == Domain.Enums.ProcessingStatus.Running)
            .OrderBy(x => x.Ticker)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<Instrument?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Instruments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<Instrument?> FindByTickerAsync(string ticker, CancellationToken ct)
    {
        string normalized = (ticker ?? string.Empty).Trim().ToUpperInvariant();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Instruments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Ticker == normalized, ct).ConfigureAwait(false);
    }

    public async Task AddAsync(Instrument instrument, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Instruments.AddAsync(instrument, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Instrument instrument, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        instrument.UpdatedAtUtc = DateTime.UtcNow;
        db.Instruments.Update(instrument);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Instruments.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        // Deleting only the instrument row left its positions, trades and signals
        // behind: the orphaned PnL stayed in the pooled equity and the signal log
        // for ever, attributed to an instrument that no longer exists.
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.OpenPositions.Where(x => x.InstrumentId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.TradeLogs.Where(x => x.InstrumentId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.SignalLogs.Where(x => x.InstrumentId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.Instruments.Remove(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task SetProcessingStatusAsync(Domain.Enums.ProcessingStatus status, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Instruments
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ProcessingStatus, status), ct)
            .ConfigureAwait(false);
    }
}

public sealed class EfPositionRepository : IPositionRepository
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public EfPositionRepository(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<OpenPosition?> GetOpenAsync(Guid instrumentId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.OpenPositions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.InstrumentId == instrumentId, ct).ConfigureAwait(false);
    }

    public async Task SaveAsync(OpenPosition position, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.OpenPositions.FirstOrDefaultAsync(x => x.InstrumentId == position.InstrumentId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            await db.OpenPositions.AddAsync(position, ct).ConfigureAwait(false);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(position);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid instrumentId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.OpenPositions.FirstOrDefaultAsync(x => x.InstrumentId == instrumentId, ct).ConfigureAwait(false);
        if (entity is not null)
        {
            db.OpenPositions.Remove(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class EfTradeLogRepository : ITradeLogRepository
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public EfTradeLogRepository(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    public async Task AddAsync(TradeLogEntry entry, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.TradeLogs.AddAsync(entry, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TradeLogEntry>> GetByInstrumentAsync(Guid instrumentId, int limit, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.TradeLogs.AsNoTracking()
            .Where(x => x.InstrumentId == instrumentId)
            .OrderByDescending(x => x.ExitTime)
            .Take(limit)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TradeLogEntry>> GetAllByInstrumentAsync(Guid instrumentId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.TradeLogs.AsNoTracking()
            .Where(x => x.InstrumentId == instrumentId)
            .OrderBy(x => x.ExitTime)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<decimal> SumRealizedPnlAsync(Guid instrumentId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.TradeLogs.AsNoTracking()
            .Where(x => x.InstrumentId == instrumentId)
            .SumAsync(x => (decimal?)x.PnlRub, ct).ConfigureAwait(false) ?? 0m;
    }

    public async Task<decimal> SumAllRealizedPnlAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.TradeLogs.AsNoTracking()
            .SumAsync(x => (decimal?)x.PnlRub, ct).ConfigureAwait(false) ?? 0m;
    }

    public async Task<RealizedPnlByDirection> SumAllRealizedPnlByDirectionAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        decimal longs = await db.TradeLogs.AsNoTracking()
            .Where(x => x.Direction == SignalDirection.Long)
            .SumAsync(x => (decimal?)x.PnlRub, ct).ConfigureAwait(false) ?? 0m;
        decimal shorts = await db.TradeLogs.AsNoTracking()
            .Where(x => x.Direction == SignalDirection.Short)
            .SumAsync(x => (decimal?)x.PnlRub, ct).ConfigureAwait(false) ?? 0m;
        return new RealizedPnlByDirection(longs, shorts);
    }

    public async Task<IReadOnlyList<TradeLogEntry>> GetAllAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.TradeLogs.AsNoTracking()
            .OrderBy(x => x.ExitTime)
            .ToListAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Multi-table writes the engine must be able to replay after a crash, done in
/// one context and one transaction. See <see cref="ITradeJournal"/>.
/// </summary>
public sealed class EfTradeJournal : ITradeJournal
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public EfTradeJournal(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    public async Task RecordOpenAsync(OpenPosition position, SignalLogEntry signal, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var existing = await db.OpenPositions
            .FirstOrDefaultAsync(x => x.InstrumentId == position.InstrumentId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            await db.OpenPositions.AddAsync(position, ct).ConfigureAwait(false);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(position);
        }

        await db.SignalLogs.AddAsync(signal, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordCloseAsync(Guid instrumentId, TradeLogEntry trade, SignalLogEntry signal, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var position = await db.OpenPositions
            .FirstOrDefaultAsync(x => x.InstrumentId == instrumentId, ct).ConfigureAwait(false);
        if (position is not null)
        {
            db.OpenPositions.Remove(position);
        }

        await db.TradeLogs.AddAsync(trade, ct).ConfigureAwait(false);
        await db.SignalLogs.AddAsync(signal, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}

public sealed class EfSignalLogRepository : ISignalLogRepository
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public EfSignalLogRepository(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    public async Task AddAsync(SignalLogEntry entry, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.SignalLogs.AddAsync(entry, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<SignalLogEntry?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.SignalLogs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<SignalLogEntry?> GetLatestOpenedAsync(Guid instrumentId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.SignalLogs.AsNoTracking()
            .Where(x => x.InstrumentId == instrumentId && x.Type == SignalType.PositionOpened)
            .OrderByDescending(x => x.Timestamp)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SignalLogEntry>> GetByInstrumentAsync(Guid instrumentId, int limit, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.SignalLogs.AsNoTracking()
            .Where(x => x.InstrumentId == instrumentId)
            .OrderByDescending(x => x.Timestamp)
            .Take(limit)
            .OrderBy(x => x.Timestamp)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<SignalLogPage> GetPageAsync(string? ticker, int page, int pageSize, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        IQueryable<SignalLogEntry> query = db.SignalLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(ticker))
        {
            string normalized = ticker.Trim().ToUpperInvariant();
            query = query.Where(x => x.Ticker == normalized);
        }

        int total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            // Id breaks ties: ordering by Timestamp alone makes paging unstable
            // when several signals share a timestamp (rows repeat or vanish).
            .OrderByDescending(x => x.Timestamp)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return new SignalLogPage(total, page, pageSize, items);
    }

    public async Task<IReadOnlyList<string>> GetTickersAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.SignalLogs.AsNoTracking()
            .Select(x => x.Ticker)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(ct).ConfigureAwait(false);
    }
}