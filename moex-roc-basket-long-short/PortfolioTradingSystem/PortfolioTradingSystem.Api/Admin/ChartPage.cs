using System.Reflection;

namespace PortfolioTradingSystem.Api.Admin;

/// <summary>Serves the embedded price-chart page (see Admin/chart.html).</summary>
public static class ChartPage
{
    private static readonly string Html = LoadHtml();

    public static IResult Serve() => Results.Content(Html, "text/html");

    private static string LoadHtml()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().FirstOrDefault(
            n => n.EndsWith("chart.html", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded resource 'Admin/chart.html' not found.");
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}