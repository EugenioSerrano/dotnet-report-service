using System.Globalization;
using System.Text.Json.Nodes;
using ReportService.Data;
using ReportService.DataContract;
using ReportService.TemplateChecks.Sample;
using ReportService.Templates;
using ReportService.Testing.TemplatePackages;

namespace ReportService.TemplateChecks.Tests.Sample;

/// <summary>
/// The at-bound record and the image source. The at-bound derivation fills the first string
/// position it admits - anchors first, then the order of the sample's own first record - with
/// text of exactly the row bound. When that position is the field a part prints as the source
/// of an image - first in the sample record and not anchored -
/// the composed row carries that text in the image source, and the writer refuses it as it
/// refuses any value that is not an image data URI. These cases record what the derivation
/// chooses and what the writer then decides, so the take's outcome for such a package is
/// visible.
/// </summary>
public sealed class ImageSourceAtBoundTests
{
    private const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAX+XDSwAAAABJRU5ErkJggg==";

    private const string ImageFirst = """
        { "type": "object", "properties": { "image": { "type": "string" }, "sequence": { "type": "integer" }, "description": { "type": "string" }, "amount": { "type": "string" } } }
        """;

    private const string ImageFirstWithPattern = """
        { "type": "object", "properties": { "image": { "type": "string", "pattern": "^data:image/" }, "sequence": { "type": "integer" }, "description": { "type": "string" }, "amount": { "type": "string" } } }
        """;

    private const string ImageAfterText = """
        { "type": "object", "properties": { "sequence": { "type": "integer" }, "description": { "type": "string" }, "image": { "type": "string" }, "amount": { "type": "string" } } }
        """;

    private static readonly string TextFirstRecord =
        "{ \"sequence\": 1, \"description\": \"Monthly statement\", \"image\": \"" + Png + "\", \"amount\": \"10.00\" }";

    private static readonly string Record =
        "{ \"image\": \"" + Png + "\", \"sequence\": 1, \"description\": \"Monthly statement\", \"amount\": \"10.00\" }";

    [Fact]
    public void AnUnanchoredImageFieldFirstInTheContractReceivesTheBoundAndTheWriterRefusesIt()
    {
        var result = Build(ImageFirst);

        Assert.True(result.Applies);
        Assert.Equal("$.image", result.Chosen!.Path);

        var value = JsonNode.Parse(result.Document!)!["items"]![0]!["image"]!.GetValue<string>();
        var refusal = Compose(value);

        Assert.NotNull(refusal);
        Assert.Equal(TemplateErrorCodes.ImageSourceInvalid, refusal.Code);
        Assert.Equal("part=row ordinal=1 attribute=src", refusal.Message);
    }

    [Fact]
    public void ADataUriPatternOnTheImageFieldDoesNotKeepTheBoundAwayFromIt()
    {
        var result = Build(ImageFirstWithPattern);

        Assert.True(result.Applies);
        Assert.Equal("$.image", result.Chosen!.Path);
    }

    [Fact]
    public void AnImageFieldAfterATextFieldIsNotChosenAndTheRowComposes()
    {
        var result = Build(ImageAfterText, TextFirstRecord);

        Assert.True(result.Applies);
        Assert.Equal("$.description", result.Chosen!.Path);

        var value = JsonNode.Parse(result.Document!)!["items"]![0]!["image"]!.GetValue<string>();
        Assert.Null(Compose(value));
    }

    private static TemplateGuardException? Compose(string image)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        var writer = new GuardedWriter(text, TimeProvider.System, ImageSourcePolicy.Pdf);
        writer.BeginPart(TemplatePart.Row, 1);
        try
        {
            writer.Write("<tr><td><img src=\"");
            writer.Write(HtmlEncodingPolicy.Encode(image));
            writer.Write("\"></td></tr>");
            writer.Complete();
            return null;
        }
        catch (TemplateGuardException refusal)
        {
            return refusal;
        }
    }

    private static AtBoundSampleResult Build(string recordSchema, string? record = null)
    {
        using var package = Packages.WritePackage(AtBoundFixtures.Package(recordSchema, [record ?? Record], itemAnchor: null));
        var candidate = Packages.ReadCandidate(package.Directory);
        var schemaBytes = File.ReadAllBytes(Path.Combine(package.Directory, "schema.json"));
        var compilation = DataContractCompiler.Compile(schemaBytes, candidate.SourceDescriptorSha256, "schema.json");
        Assert.True(compilation.Profile.IsValid, "the fixture's schema is not inside the profile: " + compilation.Profile.Rule);
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(package.Directory, "schema.json")))!;
            var sample = File.ReadAllBytes(Path.Combine(package.Directory, "sample.json"));
            return AtBoundSample.Build(new AtBoundSampleInput(candidate, compilation.Contract!, root, sample, PdfRowBound.Bytes, []));
        }
        finally
        {
            compilation.Contract!.Dispose();
        }
    }
}
