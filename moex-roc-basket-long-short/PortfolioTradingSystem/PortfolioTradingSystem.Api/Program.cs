using PortfolioTradingSystem.Api.Admin;
using PortfolioTradingSystem.Api.Auth;
using PortfolioTradingSystem.Api.Middleware;
using PortfolioTradingSystem.Application.DependencyInjection;
using PortfolioTradingSystem.Infrastructure.DependencyInjection;
using PortfolioTradingSystem.Infrastructure.Persistence;
using Serilog;
using Serilog.Events;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services
    .AddApplication(builder.Configuration)
    .AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseSerilogRequestLogging(options =>
{
    options.GetLevel = (ctx, _, ex) =>
        ex is not null || ctx.Response.StatusCode >= 400 || ctx.Request.Method != HttpMethods.Get
            ? LogEventLevel.Information
            : LogEventLevel.Verbose;
});
app.UseMiddleware<ErrorLoggingMiddleware>();
app.UseMiddleware<BasicAuthMiddleware>();
app.MapAdminEndpoints();

var dbInitializer = app.Services.GetRequiredService<IDbInitializer>();
await dbInitializer.InitializeAsync(app.Lifetime.ApplicationStopping);

var logger = app.Logger;
string? urls = builder.Configuration["Urls"] ?? builder.Configuration["ASPNETCORE_URLS"];
string? adminPassword = builder.Configuration["Admin:Password"];
if (string.IsNullOrEmpty(adminPassword))
{
    logger.LogError(
        "Admin:Password is not configured - every request will be rejected. "
        + "Set Admin__Password before using the panel.");
}
else if (adminPassword == "admin")
{
    logger.LogWarning("Admin:Password is still the sample value; change it before exposing the panel.");
}

if (urls is not null
    && (urls.Contains("0.0.0.0", StringComparison.Ordinal) || urls.Contains("[::]", StringComparison.Ordinal))
    && !urls.Contains("https://", StringComparison.OrdinalIgnoreCase))
{
    logger.LogWarning(
        "Listening on {Urls} over plain HTTP: Basic credentials travel in clear text. "
        + "Bind to 127.0.0.1 and terminate TLS in a reverse proxy.", urls);
}

logger.LogInformation("Portfolio Trading System starting; listening on {Urls}", urls);
await app.RunAsync();