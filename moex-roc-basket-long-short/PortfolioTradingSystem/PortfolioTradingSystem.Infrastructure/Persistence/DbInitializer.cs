using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PortfolioTradingSystem.Domain.Enums;
using PortfolioTradingSystem.Domain.Models;

namespace PortfolioTradingSystem.Infrastructure.Persistence;

/// <summary>Creates the schema (EnsureCreated) and idempotently seeds the 39 tradable stocks.</summary>
public interface IDbInitializer
{
    Task InitializeAsync(CancellationToken ct);
}

public sealed class DbInitializer : IDbInitializer
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger<DbInitializer> _logger;

    public DbInitializer(IDbContextFactory<AppDbContext> factory, ILogger<DbInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            bool created = await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
            if (created)
            {
                _logger.LogInformation("Database schema created");
            }

            var existing = await db.Instruments.Select(x => x.Ticker).ToListAsync(ct).ConfigureAwait(false);
            if (existing.Any())
                return;

            int added = 0;
            foreach (var ticker in SeedTickers)
            {
                if (existing.Contains(ticker))
                {
                    continue;
                }

                db.Instruments.Add(new Instrument
                {
                    Ticker = ticker,
                    ProcessingStatus = ProcessingStatus.Paused,
                });
                added++;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Database initialized: {Existing} instruments present, {Added} new seeds added", existing.Count, added);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database initialization failed");
            throw;
        }
    }

    /// <summary>
    /// ORIG U NEW minus the IMOEX benchmark (universe.py). All dropped in are
    /// inactive + paused; the admin panel activates instruments and fills FIGIs.
    /// </summary>
    private static readonly string[] SeedTickers =
    {
        // ORIG (24)
        "AFLT", "ALRS", "CBOM", "CHMF", "FEES", "GAZP", "GMKN", "IRAO",
        "LKOH", "MGNT", "MTSS", "NLMK", "NVTK", "PLZL", "POLY", "POSI",
        "ROSN", "RUAL", "SBER", "SBERP", "SNGSP", "TATN", "TRNFP", "VTBR",
        // NEW (15)
        "MOEX", "SIBN", "MAGN", "PIKK", "SMLT", "MRKC", "VKCO", "HYDR",
        "TGKA", "SNGS", "OGKB", "KMAZ", "MVID", "RTKMP", "BSPB",
    };
}
