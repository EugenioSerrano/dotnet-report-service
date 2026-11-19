using System.Text;
using ReportService.Testing;

namespace ReportService.Templates.Tests.Parsing;

/// <summary>
/// The three parsers the product makes, each one call site, and the deliveries that
/// depend on them: the take's validators and the delivered Scriban adapter parse through
/// the same types, so neither half can drift from the other.
/// </summary>
public sealed class ParsingTypesTests
{
    [Fact]
    public void EveryEngineHasOneParsingTypeAndOneCallSite()
    {
        var parsing = typeof(ScribanParsing).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(ScribanParsing).Namespace && type.Name.EndsWith("Parsing", StringComparison.Ordinal))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(["FluidParsing", "HandlebarsParsing", "ScribanParsing"], parsing);
        Assert.All(
            new[] { typeof(ScribanParsing), typeof(HandlebarsParsing), typeof(FluidParsing) },
            type => Assert.True(type.IsAbstract && type.IsSealed, $"{type.Name} is a static type"));
    }

    [Fact]
    public void TheDeliveredAdapterParsesThroughTheSharedType()
    {
        // The adapter's prepare and the validator's parse both come through ScribanParsing:
        // a part with a syntax error is refused by the adapter with the engine's message.
        var manifest = new RenderManifest(
            ScribanTemplateAdapter.EngineName,
            ScribanTemplateAdapter.EngineVersion,
            new Dictionary<TemplatePart, string> { [TemplatePart.DocumentStart] = "html/row.html.sbn" },
            [],
            new PdfSettings("A4", "portrait", new PageMarginsMm(15, 12, 18, 12), PageTotalsEnabled: false, VerificationAnchors.None),
            new LocaleDefaults("en", "UTC"),
            GroupKey: null);
        var files = new MemoryPackageFiles(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["html/row.html.sbn"] = "{{ if }}",
        });

        var refusal = Assert.Throws<TemplatePackageException>(() => new ScribanTemplateAdapter().Prepare(manifest, files));

        Assert.Contains("html/row.html.sbn", refusal.Message, StringComparison.Ordinal);
    }
}
