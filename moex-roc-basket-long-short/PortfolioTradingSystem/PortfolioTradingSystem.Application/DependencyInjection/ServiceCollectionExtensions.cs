using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PortfolioTradingSystem.Application.Configuration;
using PortfolioTradingSystem.Application.Engine;
using PortfolioTradingSystem.Domain.Strategy;

namespace PortfolioTradingSystem.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StrategyOptions>().Bind(configuration.GetSection(StrategyOptionsSection));

        services.AddOptions<TinkoffOptions>().Bind(configuration.GetSection(TinkoffOptions.SectionName));
        services.AddOptions<TelegramOptions>().Bind(configuration.GetSection(TelegramOptions.SectionName));
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddOptions<AdminOptions>().Bind(configuration.GetSection(AdminOptions.SectionName));
        services.AddOptions<EngineOptions>().Bind(configuration.GetSection(EngineOptions.SectionName));

        services.AddSingleton<IMetricsStore, InMemoryMetricsStore>();
        services.AddSingleton<TelegramMessageFormatter>();
        services.AddSingleton<EngineSupervisor>();
        services.AddSingleton<IEngineSupervisor>(sp => sp.GetRequiredService<EngineSupervisor>());
        services.AddHostedService(sp => sp.GetRequiredService<EngineSupervisor>());
        return services;
    }

    private const string StrategyOptionsSection = "Strategy";
}