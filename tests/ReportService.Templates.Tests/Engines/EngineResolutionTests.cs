using ReportService.Testing;

namespace ReportService.Templates.Tests;

/// <summary>
/// The decision of which engine composes a block: the version's own declaration, the pin's
/// cross-check and the engines the deployment offers. Every refusal happens before anything
/// is composed and names the field and the engines, never a value.
/// </summary>
public sealed class EngineResolutionTests
{
    private static readonly TemplateEngineAdapterRegistry Registry = TemplateEngineAdapters.Registry();

    [Theory]
    [InlineData("scriban", "7.5.1", "ScribanTemplateAdapter")]
    [InlineData("handlebars", "2.4.3", "HandlebarsTemplateAdapter")]
    [InlineData("liquid", "2.40.0", "FluidTemplateAdapter")]
    public void TheVersionDeclaresOneEngineAndTheDeploymentOffersIt(string engine, string version, string adapter)
    {
        var decision = EngineResolution.Resolve(Manifest(engine, version), engine, Registry);

        Assert.True(decision.Resolved);
        Assert.Equal(adapter, decision.Adapter!.GetType().Name);
        Assert.Equal(engine, decision.Adapter.Engine);
        Assert.Equal(version, decision.Adapter.Version);
    }

    [Fact]
    public void AVersionThatDeclaresNoEngineIsRefusedBeforeAnythingIsComposed()
    {
        var decision = EngineResolution.Resolve(Manifest(string.Empty, "7.5.1"), pinEngine: null, Registry);

        Assert.False(decision.Resolved);
        Assert.Equal(EngineResolution.TemplateInvalid, decision.Code);
        Assert.Equal(EngineResolution.EngineUndeclared, decision.Reason);
        Assert.Contains("field=engine", decision.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("razor")]
    [InlineData("Razor")]
    [InlineData("scriban ")]
    [InlineData("liquid_fluid")]
    public void AnEngineTheDeploymentDoesNotOfferIsRefusedAndTheNamesAreListed(string engine)
    {
        var decision = EngineResolution.Resolve(Manifest(engine, "1.0.0"), pinEngine: null, Registry);

        Assert.False(decision.Resolved);
        Assert.Equal(EngineResolution.UnsupportedCapability, decision.Code);
        Assert.Equal(EngineResolution.EngineUnsupported, decision.Reason);
        Assert.Contains("offered=handlebars", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APinThatRecordsAnotherEngineThanTheTakenCopyDeclaresIsRefused()
    {
        var decision = EngineResolution.Resolve(Manifest("scriban", "7.5.1"), pinEngine: "handlebars", Registry);

        Assert.False(decision.Resolved);
        Assert.Equal(EngineResolution.TemplateInvalid, decision.Code);
        Assert.Equal(EngineResolution.EngineMismatch, decision.Reason);
        Assert.Contains("pinned=handlebars", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APinThatRecordsTheSameEngineIsAccepted()
    {
        var decision = EngineResolution.Resolve(Manifest("liquid", "2.40.0"), pinEngine: "liquid", Registry);

        Assert.True(decision.Resolved);
    }

    [Fact]
    public void ADeclaredEngineVersionWhoseMajorDiffersIsRefused()
    {
        var decision = EngineResolution.Resolve(Manifest("scriban", "8.0.0"), pinEngine: null, Registry);

        Assert.False(decision.Resolved);
        Assert.Equal(EngineResolution.UnsupportedCapability, decision.Code);
        Assert.Equal(EngineResolution.EngineVersion, decision.Reason);
        Assert.Contains("declared=8.0.0", decision.Message, StringComparison.Ordinal);
        Assert.Contains("offered=7.5.1", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredEngineVersionWithTheSameMajorAndAHigherMinorIsAccepted()
    {
        var decision = EngineResolution.Resolve(Manifest("handlebars", "2.9.4"), pinEngine: null, Registry);

        Assert.True(decision.Resolved);
    }

    [Fact]
    public void AVersionThatDeclaresNoEngineVersionRunsTheAdaptersOwnVersion()
    {
        var decision = EngineResolution.Resolve(Manifest("scriban", string.Empty), pinEngine: null, Registry);

        Assert.True(decision.Resolved);
        Assert.Equal("7.5.1", decision.Adapter!.Version);
    }

    [Fact]
    public void ADeploymentWithoutTheDeclaredEngineRefusesIt()
    {
        var registry = new TemplateEngineAdapterRegistry([new ScribanTemplateAdapter()]);

        var decision = EngineResolution.Resolve(Manifest("handlebars", "2.4.3"), pinEngine: null, registry);

        Assert.False(decision.Resolved);
        Assert.Equal(EngineResolution.EngineUnsupported, decision.Reason);
        Assert.Contains("offered=scriban", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheThreeAdaptersAreTheThreeEnginesAndEachReportsTheVersionItRuns()
    {
        var all = TemplateEngineAdapters.All();

        Assert.Equal(3, all.Count);
        Assert.Equal(["handlebars", "liquid", "scriban"], all.Select(adapter => adapter.Engine).Order(StringComparer.Ordinal));
        Assert.Equal("7.5.1", all.Single(adapter => adapter.Engine == "scriban").Version);
        Assert.Equal("2.4.3", all.Single(adapter => adapter.Engine == "handlebars").Version);
        Assert.Equal("2.40.0", all.Single(adapter => adapter.Engine == "liquid").Version);
        Assert.Equal("handlebars,liquid,scriban", Registry.OfferedNames);
    }

    [Fact]
    public void TwoAdaptersForOneEngineAreRefused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new TemplateEngineAdapterRegistry([new ScribanTemplateAdapter(), new ScribanTemplateAdapter()]));
    }

    private static RenderManifest Manifest(string engine, string engineVersion) =>
        new(
            engine,
            engineVersion,
            new Dictionary<TemplatePart, string>(),
            [],
            new PdfSettings("A4", "portrait", new PageMarginsMm(15, 12, 18, 12), PageTotalsEnabled: false, VerificationAnchors.None),
            new LocaleDefaults("es-AR", "America/Argentina/Buenos_Aires"),
            GroupKey: null);
}
