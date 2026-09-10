using System.Reflection;

namespace PortfolioTradingSystem.Api.Admin;

/// <summary>Serves the embedded total-equity chart page (see Admin/equity.html).</summary>
public static class EquityPage
{
    private static readonly string Html = LoadHtml();

    public static IResult Serve() => Results.Content(Html, "text/html");

    private static string LoadHtml()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().FirstOrDefault(
            n => n.EndsWith("equity.html", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded resource 'Admin/equity.html' not found.");
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}