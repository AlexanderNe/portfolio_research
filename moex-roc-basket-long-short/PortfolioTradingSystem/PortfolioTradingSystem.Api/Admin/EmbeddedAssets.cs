using System.Collections.Concurrent;
using System.Reflection;

namespace PortfolioTradingSystem.Api.Admin;

/// <summary>
/// Reads the admin UI text assets (Admin/*.html|.css|.js) embedded in this
/// assembly. The files are embedded so they ship inside Api.dll and are served
/// by the endpoints in <see cref="AdminEndpoints"/>.
/// </summary>
internal static class EmbeddedAssets
{
    private static readonly Assembly Assembly = typeof(EmbeddedAssets).Assembly;

    // Embedded resource lookup runs once per file name; the contents never change
    // at runtime, so cache them.
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string ReadText(string fileName) =>
        Cache.GetOrAdd(fileName, static file =>
        {
            string resourceName = Assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("." + file, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Embedded resource 'Admin/{file}' not found.");
            using var stream = Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Resource '{resourceName}' not found.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });

    /// <summary>Build timestamp stamped into the admin header via $BUILD_LABEL$.</summary>
    public static string BuildLabel()
    {
        string build = Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), inherit: false)
            .Cast<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")
            ?.Value ?? "unknown";
        return $"Built {build} UTC";
    }
}
