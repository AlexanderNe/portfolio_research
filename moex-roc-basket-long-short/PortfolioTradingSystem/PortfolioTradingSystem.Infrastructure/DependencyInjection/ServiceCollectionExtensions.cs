using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PortfolioTradingSystem.Application.Ports;
using PortfolioTradingSystem.Infrastructure.MarketData;
using PortfolioTradingSystem.Infrastructure.Persistence;
using PortfolioTradingSystem.Infrastructure.Telegram;
using PortfolioTradingSystem.Infrastructure.Tickers;
using PortfolioTradingSystem.Infrastructure.Tinkoff;

namespace PortfolioTradingSystem.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        string connectionString = configuration["Database:ConnectionString"]
            ?? configuration.GetConnectionString("Database")
            ?? "Host=localhost;Port=5432;Database=portfolio_trading;Username=postgres;Password=postgres";

        services.AddDbContextFactory<AppDbContext>(options => options.UseNpgsql(connectionString));

        services.AddSingleton<TinkoffConnection>();
        services.AddSingleton<IMarketDataGateway, TinkoffMarketDataMultiplexer>();
        services.AddSingleton<IHistoricDailyBarsProvider, TinkoffHistoricDailyBarsProvider>();
        services.AddSingleton<ITelegramGateway, TelegramGateway>();
        services.AddSingleton<ITickerResolver, TinkoffTickerResolver>();

        services.AddSingleton<IInstrumentRepository, EfInstrumentRepository>();
        services.AddSingleton<IPositionRepository, EfPositionRepository>();
        services.AddSingleton<ITradeLogRepository, EfTradeLogRepository>();
        services.AddSingleton<ISignalLogRepository, EfSignalLogRepository>();

        services.AddSingleton<IDbInitializer, DbInitializer>();
        return services;
    }
}