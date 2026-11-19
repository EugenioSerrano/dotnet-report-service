using System.Text;
using ReportService.Testing;

namespace ReportService.Templates.Tests.Parsing;

/// <summary>
/// The take's parse and the render's prepare must answer the same verdict for the same
/// text: a part the validator accepts is one the adapter can prepare, and one it refuses
/// is refused in both halves. The corpus carries a part that is right, one that is not,
/// and one with an include.
/// </summary>
public sealed class ParsingConfigurationAgreementTests
{
    public static TheoryData<string> Corpus => new()
    {
        "<html><body>",
        "<tr><td>{{ item.quantity }}</td></tr>",
        "{{ if }}",
        "{{ include \"partials/head.sbn\" }}",
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void TheTakeAndTheRenderAnswerTheSameVerdictForEveryPart(string source)
    {
        var validatorAccepts = ValidatorAccepts(source);
        var adapterAccepts = AdapterAccepts(source);

        Assert.Equal(validatorAccepts, adapterAccepts);
    }

    [Fact]
    public void TheSameTextIsAcceptedByBothHalves()
    {
        Assert.True(ValidatorAccepts("<tr><td>{{ item.quantity }}</td></tr>"));
        Assert.True(AdapterAccepts("<tr><td>{{ item.quantity }}</td></tr>"));
    }

    [Fact]
    public void TheSameTextIsRefusedByBothHalves()
    {
        Assert.False(ValidatorAccepts("{{ if }}"));
        Assert.False(AdapterAccepts("{{ if }}"));
    }

    private static bool ValidatorAccepts(string source) =>
        new ScribanTemplateValidator().Parse("html/row.html.sbn", source) is PartParseResult.Parsed;

    private static bool AdapterAccepts(string source)
    {
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
            ["html/row.html.sbn"] = source,
        });

        try
        {
            _ = new ScribanTemplateAdapter().Prepare(manifest, files);
            return true;
        }
        catch (TemplatePackageException)
        {
            return false;
        }
    }
}
