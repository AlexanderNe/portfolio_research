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
logger.LogInformation("Portfolio Trading System starting; listening on {Urls}", builder.Configuration["Urls"]);
await app.RunAsync();