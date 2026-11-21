using ReportService.TemplatePackages.Manifest;

namespace ReportService.Templates.Tests.Engines;

/// <summary>
/// The one catalog of the recorded engine version, fed from the adapters the build ships:
/// it answers what each adapter reports - 7.5.1, 2.4.3, 2.40.0 - in the enum's order, the
/// feed is idempotent, and a partial, repeated or undefined engine is refused before the
/// table moves. Before a process feeds it, it answers the same three pins.
/// </summary>
public sealed class TemplateEngineCatalogFeedTests
{
    [Fact]
    public void TheCatalogAnswersWhatEachAdapterReports()
    {
        TemplateEngineCatalogFeed.Apply();

        foreach (var adapter in TemplateEngineAdapters.All())
        {
            Assert.True(TemplateEngineNames.TryParse(adapter.Engine, out var engine));
            Assert.Equal(adapter.Version, TemplateEngineCatalog.VersionOf(engine.Value));
        }

        Assert.Equal(
            ["scriban", "handlebars", "liquid"],
            TemplateEngineCatalog.Registration.Select(row => TemplateEngineNames.ToManifestName(row.Key)));
        Assert.Equal(["7.5.1", "2.4.3", "2.40.0"], TemplateEngineCatalog.Registration.Select(row => row.Value));
    }

    [Fact]
    public void TheFeedIsIdempotent()
    {
        TemplateEngineCatalogFeed.Apply();
        TemplateEngineCatalogFeed.Apply();

        Assert.Equal("7.5.1", TemplateEngineCatalog.VersionOf(TemplateEngine.Scriban));
        Assert.Equal("2.4.3", TemplateEngineCatalog.VersionOf(TemplateEngine.Handlebars));
        Assert.Equal("2.40.0", TemplateEngineCatalog.VersionOf(TemplateEngine.LiquidFluid));
    }

    [Fact]
    public void ARepeatedEngineIsRefusedAndTheTableIsUnchanged()
    {
        TemplateEngineCatalogFeed.Apply();

        Assert.Throws<InvalidOperationException>(() => TemplateEngineCatalog.Register(
        [
            new KeyValuePair<TemplateEngine, string>(TemplateEngine.Scriban, "9.9.9"),
            new KeyValuePair<TemplateEngine, string>(TemplateEngine.Scriban, "9.9.9"),
            new KeyValuePair<TemplateEngine, string>(TemplateEngine.Handlebars, "2.4.3"),
            new KeyValuePair<TemplateEngine, string>(TemplateEngine.LiquidFluid, "2.40.0"),
        ]));

        Assert.Equal("7.5.1", TemplateEngineCatalog.VersionOf(TemplateEngine.Scriban));
    }

    [Fact]
    public void APartialFeedIsRefused()
    {
        TemplateEngineCatalogFeed.Apply();

        Assert.Throws<InvalidOperationException>(() => TemplateEngineCatalog.Register(
            [new KeyValuePair<TemplateEngine, string>(TemplateEngine.Scriban, "7.5.1")]));

        Assert.Equal("2.40.0", TemplateEngineCatalog.VersionOf(TemplateEngine.LiquidFluid));
    }

    [Fact]
    public void AnEngineTheTypeDoesNotDefineIsRefused()
    {
        TemplateEngineCatalogFeed.Apply();

        Assert.Throws<InvalidOperationException>(() => TemplateEngineCatalog.Register(
        [
            new KeyValuePair<TemplateEngine, string>((TemplateEngine)99, "1.0.0"),
            new KeyValuePair<TemplateEngine, string>(TemplateEngine.Scriban, "7.5.1"),
            new KeyValuePair<TemplateEngine, string>(TemplateEngine.Handlebars, "2.4.3"),
            new KeyValuePair<TemplateEngine, string>(TemplateEngine.LiquidFluid, "2.40.0"),
        ]));

        Assert.Equal("7.5.1", TemplateEngineCatalog.VersionOf(TemplateEngine.Scriban));
    }

    [Fact]
    public void AnEmptyVersionIsRefused()
    {
        Assert.Throws<ArgumentException>(() => TemplateEngineCatalog.Register(
        [
            new KeyValuePair<TemplateEngine, string>(TemplateEngine.Scriban, " "),
            new KeyValuePair<TemplateEngine, string>(TemplateEngine.Handlebars, "2.4.3"),
            new KeyValuePair<TemplateEngine, string>(TemplateEngine.LiquidFluid, "2.40.0"),
        ]));
    }
}
