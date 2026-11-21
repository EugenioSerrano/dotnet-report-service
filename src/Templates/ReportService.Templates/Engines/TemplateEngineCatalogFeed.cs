using ReportService.TemplatePackages.Manifest;

namespace ReportService.Templates;

/// <summary>
/// Feeds the recorded engine versions from the adapters a process registered: the one
/// place the composition says which version each engine runs, so no constant can
/// drift from the package the build resolved. The catalog keeps the answer; this class
/// only reads the adapters' own <c>Version</c> and hands it over, once, in the enum's
/// order.
/// </summary>
public static class TemplateEngineCatalogFeed
{
    /// <summary>Feeds the catalog from <paramref name="adapters"/>, one entry per engine.</summary>
    /// <param name="adapters">The adapters this process registered.</param>
    /// <exception cref="InvalidOperationException">An adapter names no engine this build defines.</exception>
    public static void Apply(IEnumerable<ITemplateEngineAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var versions = new List<KeyValuePair<TemplateEngine, string>>();
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (!TemplateEngineNames.TryParse(adapter.Engine, out var engine))
            {
                throw new InvalidOperationException(
                    $"The adapter '{adapter.Engine}' names no engine this build offers, so the catalog cannot record its version.");
            }

            versions.Add(new KeyValuePair<TemplateEngine, string>(engine.Value, adapter.Version));
        }

        TemplateEngineCatalog.Register(versions);
    }

    /// <summary>Feeds the catalog from the adapters this build ships.</summary>
    public static void Apply() => Apply(TemplateEngineAdapters.All());
}
