using System.Text.Json;
using ReportService.TemplatePackages.Manifest;

namespace ReportService.TemplatePackages.Tests.Manifest;

/// <summary>
/// The stored manifest and its typed view: the document's own members in the
/// document's own order, a view of the members this build reads, and an engine
/// this deployment cannot render refused as such.
/// </summary>
public sealed class TemplateManifestJsonTests
{
    private const string PilotWithOwner = """
        {"manifestVersion":"1.0","id":"invoice-energy","version":"1.1.0","owner":"billing-system","shared":false,
        "engine":"scriban","engineVersion":"7.5.1",
        "dataContract":{"profile":"header-items-v1","version":"1.0","schema":"schema.json"},
        "outputs":{"html":{"parts":{"documentStart":"html/document-start.html.sbn","row":"html/row.html.sbn","documentEnd":"html/document-end.html.sbn"},"styles":["html/styles.css"]},
        "pdf":{"paper":"A4","marginMm":{"top":15,"right":12,"bottom":18,"left":12},"pageTotals":{"enabled":false},
        "verification":{"anchors":{"header":["$.customer.name"],"item":["$.description","$.amount"]}}}},
        "aggregates":[{"name":"total","field":"amount","operation":"sum","scope":"document"}],
        "localeDefaults":{"culture":"es-AR","timeZone":"America/Argentina/Buenos_Aires"}}
        """;

    [Fact]
    public void TheStoredFormKeepsTheDocumentsOrderAndItsUnknownMembers()
    {
        using var document = JsonDocument.Parse("""{"id":"a","documentStart":"x","nested":{"row":"y"},"list":[1,2]}""");
        var stored = TemplateManifestJson.Write(JsonSerializer.SerializeToNode(document.RootElement));

        Assert.Equal("""{"id":"a","documentStart":"x","nested":{"row":"y"},"list":[1,2]}""", stored);
        Assert.Contains("documentStart", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentWithoutMembersIsRefusedRatherThanStoredEmpty()
    {
        Assert.Throws<ArgumentNullException>(() => TemplateManifestJson.Write(null));
        Assert.Throws<ArgumentException>(() => TemplateManifestJson.Parse(string.Empty));
        Assert.Throws<JsonException>(() => TemplateManifestJson.ReadTyped("[]"));
    }

    [Fact]
    public void TheTypedViewReadsEveryMemberThisBuildKnows()
    {
        var manifest = TemplateManifestJson.ReadTyped(PilotWithOwner);

        Assert.Equal("1.0", manifest.ManifestVersion);
        Assert.Equal("invoice-energy", manifest.Id);
        Assert.Equal("1.1.0", manifest.Version);
        Assert.Equal("billing-system", manifest.Owner);
        Assert.False(manifest.Shared);
        Assert.Equal(TemplateEngine.Scriban, manifest.Engine);
        Assert.Equal("7.5.1", manifest.DeclaredEngineVersion);
        Assert.Equal("schema.json", manifest.DataContract?.Schema);
        Assert.Equal("header-items-v1", manifest.DataContract?.Profile);
        Assert.Equal("html/row.html.sbn", manifest.Outputs?.Html?.Parts?.Row);
        Assert.Equal("html/styles.css", Assert.Single(manifest.Outputs!.Html!.Styles));
        Assert.Equal("A4", manifest.Outputs?.Pdf?.Paper);
        Assert.Equal(15m, manifest.Outputs?.Pdf?.MarginMm?.Top);
        Assert.False(manifest.Outputs?.Pdf?.PageTotals?.Enabled);
        Assert.Equal("$.customer.name", Assert.Single(manifest.Outputs!.Pdf!.Verification!.Anchors!.Header!));
        Assert.Equal(2, manifest.Outputs.Pdf.Verification.Anchors.Item!.Count);
        Assert.Equal("sum", Assert.Single(manifest.Aggregates).Operation);
        Assert.Equal("es-AR", manifest.LocaleDefaults?.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", manifest.LocaleDefaults?.TimeZone);
    }

    [Fact]
    public void ADocumentWithoutTheOptionalMembersLeavesThemEmpty()
    {
        var manifest = TemplateManifestJson.ReadTyped("""{"manifestVersion":"1.0","id":"pilot-simple","version":"1.0.0"}""");

        Assert.Null(manifest.Owner);
        Assert.False(manifest.Shared);
        Assert.Null(manifest.Engine);
        Assert.Null(manifest.Outputs);
        Assert.Null(manifest.DataContract);
        Assert.Empty(manifest.Aggregates);
        Assert.Null(manifest.Partitioning);
    }

    [Theory]
    [InlineData("razor")]
    [InlineData("Scriban")]
    [InlineData("SCRIBAN")]
    [InlineData("liquid_fluid")]
    public void AnEngineThisDeploymentCannotRenderIsRefused(string engine)
    {
        Assert.Throws<JsonException>(
            () => TemplateManifestJson.ReadTyped("""{"manifestVersion":"1.0","engine":"**ENGINE**"}""".Replace("**ENGINE**", engine, StringComparison.Ordinal)));
    }

    [Fact]
    public void AnEngineThatIsNotAStringIsRefused()
    {
        Assert.Throws<JsonException>(() => TemplateManifestJson.ReadTyped("""{"manifestVersion":"1.0","engine":3}"""));
    }

    [Fact]
    public void TheManifestIsReadBackFromItsOwnStoredForm()
    {
        var stored = TemplateManifestJson.Write(TemplateManifestJson.Parse(PilotWithOwner));
        var manifest = TemplateManifestJson.ReadTyped(stored);

        Assert.Equal("invoice-energy", manifest.Id);
        Assert.Equal(TemplateEngine.Scriban, manifest.Engine);
    }
}
