using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PortfolioTradingSystem.Api.Middleware;

/// <summary>
/// Logs any unhandled exception and returns a JSON 500 without leaking stack traces.
/// Placed outermost so it also covers auth, routing and endpoint failures.
/// </summary>
public sealed class ErrorLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorLoggingMiddleware> _logger;

    public ErrorLoggingMiddleware(RequestDelegate next, ILogger<ErrorLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception while processing {Method} {Path}", context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new { error = "internal_error", message = "An unexpected error occurred." }),
                context.RequestAborted);
        }
    }
}