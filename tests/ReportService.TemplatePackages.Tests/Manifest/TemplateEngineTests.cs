using System.Text.Json;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Manifest;

namespace ReportService.TemplatePackages.Tests.Manifest;

/// <summary>The two vocabularies of an engine name, and the version each one renders with.</summary>
public sealed class TemplateEngineTests
{
    [Theory]
    [InlineData("scriban", TemplateEngine.Scriban)]
    [InlineData("handlebars", TemplateEngine.Handlebars)]
    [InlineData("liquid", TemplateEngine.LiquidFluid)]
    public void TheManifestNamesAreTheDesignsOwnWords(string name, TemplateEngine expected)
    {
        Assert.True(TemplateEngineNames.TryParse(name, out var engine));
        Assert.Equal(expected, engine);
        Assert.Equal(name, TemplateEngineNames.ToManifestName(expected));
    }

    [Theory]
    [InlineData("razor")]
    [InlineData("Scriban")]
    [InlineData(" scriban")]
    [InlineData("")]
    public void AnyOtherManifestNameIsNotAnEngineOfThisDeployment(string name)
    {
        Assert.False(TemplateEngineNames.TryParse(name, out var engine));
        Assert.Null(engine);
    }

    [Fact]
    public void NoManifestNameAtAllIsNotAnEngine()
    {
        Assert.False(TemplateEngineNames.TryParse(null, out _));
    }

    [Theory]
    [InlineData(TemplateEngine.Scriban, "SCRIBAN")]
    [InlineData(TemplateEngine.Handlebars, "HANDLEBARS")]
    [InlineData(TemplateEngine.LiquidFluid, "LIQUID_FLUID")]
    public void TheStoredTextIsTheTablesOwnVocabulary(TemplateEngine engine, string stored)
    {
        Assert.Equal(stored, TemplateEngineNames.ToStoredText(engine));
        Assert.True(TemplateEngineNames.TryParseStoredText(stored, out var read));
        Assert.Equal(engine, read);
    }

    [Theory]
    [InlineData("scriban")]
    [InlineData("RAZOR")]
    [InlineData("")]
    public void AStoredTextThatIsNotOneOfTheThreeIsRefused(string stored)
    {
        Assert.False(TemplateEngineNames.TryParseStoredText(stored, out var engine));
        Assert.Null(engine);
    }

    [Fact]
    public void TheCatalogNamesOneVersionPerEngine()
    {
        Assert.Equal("7.5.1", TemplateEngineCatalog.VersionOf(TemplateEngine.Scriban));
        Assert.Equal("2.4.3", TemplateEngineCatalog.VersionOf(TemplateEngine.Handlebars));
        Assert.Equal("2.40.0", TemplateEngineCatalog.VersionOf(TemplateEngine.LiquidFluid));
    }

    [Fact]
    public void TheConverterWritesTheManifestNameAndRefusesWhatItCannotRead()
    {
        var options = new JsonSerializerOptions { Converters = { new TemplateEngineJsonConverter() } };

        Assert.Equal("\"liquid\"", JsonSerializer.Serialize(TemplateEngine.LiquidFluid, options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TemplateEngine>("\"razor\"", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TemplateEngine>("7", options));
        Assert.Throws<ArgumentNullException>(() => new TemplateEngineJsonConverter().Write(null!, TemplateEngine.Scriban, options));
    }

    [Fact]
    public void ThePinCarriesTheValuesTheRecordFixed()
    {
        var manifest = TemplateManifestJson.ReadTyped("""{"manifestVersion":"1.0","id":"invoice-energy","version":"1.1.0"}""");
        var record = new Records.TemplateVersionRecord(
            "invoice-energy",
            "1.1.0",
            "billing-system",
            Shared: true,
            "1.0",
            TemplateEngine.Handlebars,
            TemplateEngineCatalog.HandlebarsVersion,
            "schema.json",
            new string('1', 40),
            new string('2', 40),
            new string('3', 64),
            128,
            FileCount: 2,
            TemplatePackageKeys.ForVersion("billing-system", "invoice-energy", "1.1.0", new string('3', 64)),
            manifest,
            """{"manifestVersion":"1.0","id":"invoice-energy","version":"1.1.0"}""",
            new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

        var resolvedAt = new DateTimeOffset(2026, 11, 1, 13, 0, 0, TimeSpan.Zero);
        var pin = Records.TemplatePin.From(record, resolvedAt);

        Assert.Equal("invoice-energy", pin.TemplateId);
        Assert.Equal("1.1.0", pin.ResolvedVersion);
        Assert.Equal(record.CommitSha, pin.CommitSha);
        Assert.Equal(record.PackageSha256, pin.PackageSha256);
        Assert.Equal(TemplateEngine.Handlebars, pin.Engine);
        Assert.Equal("2.4.3", pin.EngineVersion);
        Assert.Equal("schema.json", pin.SchemaRef);
        Assert.Equal(resolvedAt, pin.ResolvedAt);
        Assert.Throws<ArgumentNullException>(() => Records.TemplatePin.From(null!, resolvedAt));
    }
}
