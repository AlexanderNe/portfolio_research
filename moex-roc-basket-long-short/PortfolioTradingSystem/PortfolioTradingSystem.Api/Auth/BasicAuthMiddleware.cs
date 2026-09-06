using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioTradingSystem.Application.Configuration;

namespace PortfolioTradingSystem.Api.Auth;

/// <summary>
/// HTTP Basic authentication for every endpoint of the admin panel.
/// Password comparison is constant-time (SHA-256 digests compared via FixedTimeEquals).
/// </summary>
public sealed class BasicAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AdminOptions _options;
    private readonly ILogger<BasicAuthMiddleware> _logger;

    public BasicAuthMiddleware(RequestDelegate next, IOptions<AdminOptions> options, ILogger<BasicAuthMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string header = context.Request.Headers.Authorization.ToString();
        string user = string.Empty;
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            string encoded = header["Basic ".Length..].Trim();
            string? decoded = null;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch (FormatException)
            {
                decoded = null;
            }

            if (decoded is not null)
            {
                int colon = decoded.IndexOf(':');
                user = colon >= 0 ? decoded[..colon] : decoded;
                string pass = colon >= 0 ? decoded[(colon + 1)..] : string.Empty;
                if (SafeEquals(user, _options.Username) && SafeEquals(pass, _options.Password))
                {
                    _logger.LogDebug("Authenticated {User} from {Ip}", user, context.Connection.RemoteIpAddress);
                    await _next(context);
                    return;
                }
            }
        }

        _logger.LogWarning(
            "Authentication failed: user={User} ip={Ip} {Method} {Path}",
            user, context.Connection.RemoteIpAddress, context.Request.Method, context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{_options.Realm}\"";
    }

    private static bool SafeEquals(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);
        }

        byte[] da = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        byte[] db = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(da, db);
    }
}