using System.Collections.ObjectModel;

namespace ReportService.TemplatePackages.Manifest;

/// <summary>
/// The engine versions this deployment renders with: the one named source of the
/// <c>engine_version</c> a taken version records. A process
/// feeds it once, at composition, from the adapters it registered; before that feed
/// it answers the three pins the build ships (Scriban 7.5.1, Handlebars.Net 2.4.3,
/// Fluid.Core 2.40.0), so a process that has not composed the adapters - the API
/// host, a static check's own test - reads the version it will actually run.
/// <para>
/// The feed replaces the pins with the registered adapters' own <c>Version</c> and
/// is idempotent and ordered: one entry per engine, in the enum's order, no
/// duplicates. A partial feed, a duplicate or an engine the type does not define is
/// refused, because either would let the recorded version drift from the engine
/// that renders the block. The table is replaced as one immutable value, so a
/// render that reads it never sees half a feed.
/// </para>
/// </summary>
public static class TemplateEngineCatalog
{
    private static readonly IReadOnlyDictionary<TemplateEngine, string> Pins =
        new ReadOnlyDictionary<TemplateEngine, string>(new Dictionary<TemplateEngine, string>
        {
            [TemplateEngine.Scriban] = "7.5.1",
            [TemplateEngine.Handlebars] = "2.4.3",
            [TemplateEngine.LiquidFluid] = "2.40.0",
        });

    private static readonly IReadOnlyList<KeyValuePair<TemplateEngine, string>> PinRows =
        [.. Pins.OrderBy(entry => entry.Key)];

    private static Table _table = new(Pins, PinRows);

    /// <summary>The feed as it stands: one version per engine, in the enum's order.</summary>
    public static IReadOnlyList<KeyValuePair<TemplateEngine, string>> Registration => _table.Rows;

    /// <summary>The version that will render a package declaring <paramref name="engine"/>.</summary>
    /// <param name="engine">A declared engine.</param>
    public static string VersionOf(TemplateEngine engine) =>
        _table.Versions.TryGetValue(engine, out var version)
            ? version
            : throw new ArgumentOutOfRangeException(nameof(engine));

    /// <summary>Replaces the catalog's table with the versions the registered adapters report.</summary>
    /// <param name="engines">One version per engine this deployment offers.</param>
    /// <exception cref="InvalidOperationException">The feed is partial, repeats an engine or names one the type does not define.</exception>
    /// <exception cref="ArgumentException">A version is empty.</exception>
    public static void Register(IEnumerable<KeyValuePair<TemplateEngine, string>> engines)
    {
        ArgumentNullException.ThrowIfNull(engines);

        var byEngine = new Dictionary<TemplateEngine, string>();
        foreach (var (engine, version) in engines)
        {
            if (!Enum.IsDefined(engine))
            {
                throw new InvalidOperationException($"The catalog was fed the engine '{(int)engine}', which this build does not define.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(version);
            if (!byEngine.TryAdd(engine, version))
            {
                throw new InvalidOperationException($"The catalog was fed two versions of the engine '{TemplateEngineNames.ToManifestName(engine)}'.");
            }
        }

        foreach (var engine in Enum.GetValues<TemplateEngine>())
        {
            if (!byEngine.ContainsKey(engine))
            {
                throw new InvalidOperationException($"The catalog was fed no version of the engine '{TemplateEngineNames.ToManifestName(engine)}'.");
            }
        }

        _table = new Table(
            new ReadOnlyDictionary<TemplateEngine, string>(byEngine),
            [.. byEngine.OrderBy(entry => entry.Key)]);
    }

    private sealed record Table(
        IReadOnlyDictionary<TemplateEngine, string> Versions,
        IReadOnlyList<KeyValuePair<TemplateEngine, string>> Rows);
}
