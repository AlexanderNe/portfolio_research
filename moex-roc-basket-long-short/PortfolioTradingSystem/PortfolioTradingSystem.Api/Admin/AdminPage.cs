using System.Reflection;

namespace PortfolioTradingSystem.Api.Admin;

/// <summary>Serves the embedded browser admin panel (see Admin/admin.html).</summary>
public static class AdminPage
{
    private static readonly string Html = LoadHtml();

    public static IResult Serve() => Results.Content(Html, "text/html");

    private static string LoadHtml()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().FirstOrDefault(
            n => n.EndsWith("admin.html", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded resource 'Admin/admin.html' not found.");
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}