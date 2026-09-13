using Microsoft.EntityFrameworkCore;
using PortfolioTradingSystem.Domain.Models;

namespace PortfolioTradingSystem.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Instrument> Instruments => Set<Instrument>();

    public DbSet<OpenPosition> OpenPositions => Set<OpenPosition>();

    public DbSet<TradeLogEntry> TradeLogs => Set<TradeLogEntry>();

    public DbSet<SignalLogEntry> SignalLogs => Set<SignalLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Instrument>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Ticker).IsRequired().HasMaxLength(32);
            e.HasIndex(x => x.Ticker).IsUnique();
            e.Property(x => x.Isin).HasMaxLength(16);
            e.Property(x => x.Figi).HasMaxLength(32);
            e.Property(x => x.Uid).HasMaxLength(64);
            e.Property(x => x.ClassCode).HasMaxLength(16);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.FutureTicker).HasMaxLength(32);
            e.Property(x => x.FutureFigi).HasMaxLength(32);
            e.Property(x => x.FutureClassCode).HasMaxLength(16);
        });

        modelBuilder.Entity<OpenPosition>(e =>
        {
            e.HasKey(x => x.Id);
            // "one open position per instrument" is the core invariant of the
            // engine; let the database enforce it rather than trusting callers.
            e.HasIndex(x => x.InstrumentId).IsUnique();
            e.Property(x => x.Ticker).HasMaxLength(32);
            e.Property(x => x.EntryPrice).HasPrecision(18, 6);
            e.Property(x => x.StopLoss).HasPrecision(18, 6);
            e.Property(x => x.TakeProfit).HasPrecision(18, 6);
            e.Property(x => x.AtrAtEntry).HasPrecision(18, 6);
            e.Property(x => x.OpenCommission).HasPrecision(18, 6);
        });

        modelBuilder.Entity<TradeLogEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.InstrumentId);
            e.HasIndex(x => x.ExitTime);
            e.Property(x => x.Ticker).HasMaxLength(32);
            e.Property(x => x.EntryPrice).HasPrecision(18, 6);
            e.Property(x => x.ExitPrice).HasPrecision(18, 6);
            e.Property(x => x.PnlRub).HasPrecision(18, 6);
            e.Property(x => x.ReturnPercent).HasPrecision(18, 6);
            e.Property(x => x.Commission).HasPrecision(18, 6);
        });

        modelBuilder.Entity<SignalLogEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.InstrumentId);
            e.HasIndex(x => x.Timestamp);
            e.Property(x => x.Ticker).HasMaxLength(32);
            e.Property(x => x.Price).HasPrecision(18, 6);
            e.Property(x => x.StopLoss).HasPrecision(18, 6);
            e.Property(x => x.TakeProfit).HasPrecision(18, 6);
            e.Property(x => x.ExitPrice).HasPrecision(18, 6);
            e.Property(x => x.PnlPercent).HasPrecision(18, 6);
        });
    }
}