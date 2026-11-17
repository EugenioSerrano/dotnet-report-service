using System.Globalization;
using System.Text.Json;
using ReportService.TemplatePackages.Manifest;

namespace ReportService.TemplatePackages.Tests.Manifest;

/// <summary>
/// The one definition of the page settings a PDF template declares: the two papers
/// and their millimetres, the two orientations, the margins, the printable area a
/// page must keep, and the order in which a missing or unusable member is reported.
/// The declaration is read from the manifest's stored JSON, so a value of the wrong
/// JSON kind is reported with its path instead of being coerced.
/// </summary>
public sealed class PdfPageSettingsRulesTests
{
    private const string FieldPaper = "outputs.pdf.paper";
    private const string FieldOrientation = "outputs.pdf.orientation";
    private const string FieldMargins = "outputs.pdf.marginMm";

    [Theory]
    [InlineData("A4", "portrait", "210", "297")]
    [InlineData("A4", "landscape", "297", "210")]
    [InlineData("Letter", "portrait", "215.9", "279.4")]
    [InlineData("Letter", "landscape", "279.4", "215.9")]
    public void AnAdmittedPaperAndOrientationResolveToTheirExactMillimetres(
        string paper,
        string orientation,
        string widthMm,
        string heightMm)
    {
        var result = PdfPageSettingsRules.Resolve(Pdf(paper, orientation, Margins(15, 12, 18, 12)));

        Assert.True(result.Resolved, result.UnusableField);
        var settings = result.Settings!;
        Assert.Equal(paper, settings.PaperName, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(decimal.Parse(widthMm, CultureInfo.InvariantCulture), settings.WidthMm);
        Assert.Equal(decimal.Parse(heightMm, CultureInfo.InvariantCulture), settings.HeightMm);
    }

    [Theory]
    [InlineData("a4", "PORTRAIT")]
    [InlineData("A4", "Portrait")]
    [InlineData("LeTtEr", "landscApe")]
    public void TheNamesAreComparedWithoutCase(string paper, string orientation)
    {
        var result = PdfPageSettingsRules.Resolve(Pdf(paper, orientation, Margins(15, 12, 18, 12)));

        Assert.True(result.Resolved, result.UnusableField);
    }

    [Fact]
    public void TheDeclaredMarginsAreKeptWithTheirDigits()
    {
        var result = PdfPageSettingsRules.Resolve(Pdf("Letter", "portrait", Margins(25.4m, 19.05m, 25.4m, 19.05m)));

        Assert.True(result.Resolved, result.UnusableField);
        var settings = result.Settings!;
        Assert.Equal(25.4m, settings.MarginTopMm);
        Assert.Equal(19.05m, settings.MarginRightMm);
        Assert.Equal(25.4m, settings.MarginBottomMm);
        Assert.Equal(19.05m, settings.MarginLeftMm);
        Assert.Equal(215.9m - 19.05m - 19.05m, settings.PrintableWidthMm);
        Assert.Equal(279.4m - 25.4m - 25.4m, settings.PrintableHeightMm);
    }

    [Fact]
    public void TheOrientationsOwnNameIsTheManifestWording()
    {
        var portrait = PdfPageSettingsRules.Resolve(Pdf("A4", "portrait", Margins(15, 12, 18, 12))).Settings!;
        var landscape = PdfPageSettingsRules.Resolve(Pdf("A4", "LANDSCAPE", Margins(15, 12, 18, 12))).Settings!;

        Assert.Equal("portrait", portrait.OrientationName);
        Assert.Equal("landscape", landscape.OrientationName);
    }

    [Fact]
    public void AnAbsentDeclarationNamesThePaper()
    {
        var result = PdfPageSettingsRules.Resolve(default);

        Assert.False(result.Resolved);
        Assert.Equal(FieldPaper, result.UnusableField);
    }

    [Fact]
    public void AnAbsentPaperNamesThePaper()
    {
        var result = PdfPageSettingsRules.Resolve(Pdf(objectText: """{"orientation":"portrait","marginMm":{"top":15,"right":12,"bottom":18,"left":12}}"""));

        Assert.Equal(FieldPaper, result.UnusableField);
    }

    [Theory]
    [InlineData("15")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("\"A5\"")]
    [InlineData("\"Legal\"")]
    [InlineData("\"\"")]
    public void APaperThatIsNotOneOfTheTwoIsUnusable(string paper)
    {
        var result = PdfPageSettingsRules.Resolve(Pdf(objectText:
            "{\"paper\":" + paper + ",\"orientation\":\"portrait\",\"marginMm\":{\"top\":15,\"right\":12,\"bottom\":18,\"left\":12}}"));


        Assert.False(result.Resolved);
        Assert.Equal(FieldPaper, result.UnusableField);
    }

    [Fact]
    public void AnAbsentOrientationNamesTheOrientation()
    {
        var result = PdfPageSettingsRules.Resolve(Pdf(objectText: """{"paper":"A4","marginMm":{"top":15,"right":12,"bottom":18,"left":12}}"""));

        Assert.Equal(FieldOrientation, result.UnusableField);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("\"sideways\"")]
    [InlineData("\"\"")]
    public void AnOrientationThatIsNotOneOfTheTwoIsUnusable(string orientation)
    {
        var result = PdfPageSettingsRules.Resolve(Pdf(objectText:
            "{\"paper\":\"A4\",\"orientation\":" + orientation + ",\"marginMm\":{\"top\":15,\"right\":12,\"bottom\":18,\"left\":12}}"));


        Assert.False(result.Resolved);
        Assert.Equal(FieldOrientation, result.UnusableField);
    }

    [Fact]
    public void AnAbsentMarginBlockNamesTheMargins()
    {
        var result = PdfPageSettingsRules.Resolve(Pdf(objectText: """{"paper":"A4","orientation":"portrait"}"""));

        Assert.Equal(FieldMargins, result.UnusableField);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"15mm\"")]
    [InlineData("[]")]
    [InlineData("15")]
    public void AMarginBlockThatIsNotAnObjectIsUnusable(string margins)
    {
        var result = PdfPageSettingsRules.Resolve(Pdf(objectText: $$"""{"paper":"A4","orientation":"portrait","marginMm":{{margins}}}"""));

        Assert.Equal(FieldMargins, result.UnusableField);
    }

    [Theory]
    [InlineData("top")]
    [InlineData("right")]
    [InlineData("bottom")]
    [InlineData("left")]
    public void AMissingMarginNamesItsOwnMember(string side)
    {
        var result = PdfPageSettingsRules.Resolve(Pdf(objectText: $$"""{"paper":"A4","orientation":"portrait","marginMm":{{MarginsWithout(side)}}}"""));

        Assert.Equal(FieldMargins + "." + side, result.UnusableField);
    }

    [Theory]
    [InlineData("top", "\"15\"")]
    [InlineData("right", "true")]
    [InlineData("bottom", "[]")]
    [InlineData("left", "{}")]
    [InlineData("top", "null")]
    public void AMarginOfTheWrongJsonKindNamesItsOwnMember(string side, string value)
    {
        var result = PdfPageSettingsRules.Resolve(Pdf(objectText: $$"""{"paper":"A4","orientation":"portrait","marginMm":{{MarginsWith(side, value)}}}"""));

        Assert.Equal(FieldMargins + "." + side, result.UnusableField);
    }

    [Theory]
    [InlineData("top")]
    [InlineData("right")]
    [InlineData("bottom")]
    [InlineData("left")]
    public void ANegativeMarginNamesItsOwnMember(string side)
    {
        var result = PdfPageSettingsRules.Resolve(Pdf(objectText: $$"""{"paper":"A4","orientation":"portrait","marginMm":{{MarginsWith(side, "-0.01")}}}"""));

        Assert.Equal(FieldMargins + "." + side, result.UnusableField);
    }

    [Fact]
    public void AZeroMarginIsAdmitted()
    {
        var result = PdfPageSettingsRules.Resolve(Pdf("A4", "portrait", Margins(0, 0, 0, 0)));

        Assert.True(result.Resolved, result.UnusableField);
        Assert.Equal(0m, result.Settings!.MarginTopMm);
    }

    [Fact]
    public void APrintableWidthOfExactlyFiftyMillimetresIsAdmitted()
    {
        var result = PdfPageSettingsRules.Resolve(Pdf("A4", "portrait", Margins(0, 80, 0, 80)));

        Assert.True(result.Resolved, result.UnusableField);
        Assert.Equal(50m, result.Settings!.PrintableWidthMm);
    }

    [Fact]
    public void APrintableWidthUnderFiftyMillimetresNamesTheMargins()
    {
        var result = PdfPageSettingsRules.Resolve(Pdf("A4", "portrait", Margins(0, 80.01m, 0, 80)));

        Assert.False(result.Resolved);
        Assert.Equal(FieldMargins, result.UnusableField);
    }

    [Fact]
    public void APrintableHeightOfExactlyFiftyMillimetresIsAdmitted()
    {
        var result = PdfPageSettingsRules.Resolve(Pdf("A4", "portrait", Margins(123.5m, 0, 123.5m, 0)));

        Assert.True(result.Resolved, result.UnusableField);
        Assert.Equal(50m, result.Settings!.PrintableHeightMm);
    }

    [Fact]
    public void APrintableHeightUnderFiftyMillimetresNamesTheMargins()
    {
        var result = PdfPageSettingsRules.Resolve(Pdf("A4", "portrait", Margins(123.51m, 0, 123.5m, 0)));

        Assert.False(result.Resolved);
        Assert.Equal(FieldMargins, result.UnusableField);
    }

    [Fact]
    public void ThePaperIsCheckedBeforeTheOrientation()
    {
        var result = PdfPageSettingsRules.Resolve(Pdf(objectText: """{"paper":"A5","orientation":"sideways","marginMm":{}}"""));

        Assert.Equal(FieldPaper, result.UnusableField);
    }

    [Fact]
    public void TheOrientationIsCheckedBeforeTheMargins()
    {
        var result = PdfPageSettingsRules.Resolve(Pdf(objectText: """{"paper":"A4","orientation":"sideways","marginMm":{"top":"15"}}"""));

        Assert.Equal(FieldOrientation, result.UnusableField);
    }

    [Fact]
    public void TheMarginsAreCheckedTopRightBottomLeft()
    {
        var result = PdfPageSettingsRules.Resolve(Pdf(objectText: """{"paper":"A4","orientation":"portrait","marginMm":{"top":"15","right":-1,"bottom":false,"left":2}}"""));

        Assert.Equal(FieldMargins + ".top", result.UnusableField);
    }

    [Fact]
    public void ANegativeMarginNeverPutsItsValueInTheReportedPath()
    {
        var result = PdfPageSettingsRules.Resolve(Pdf(objectText: """{"paper":"A4","orientation":"portrait","marginMm":{"top":-12.75,"right":12,"bottom":18,"left":12}}"""));

        Assert.Equal(FieldMargins + ".top", result.UnusableField);
        Assert.DoesNotContain("-12.75", result.UnusableField, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdmittedNamesAreTheOnlyNamesTheRuleKnows()
    {
        Assert.Equal(["A4", "Letter"], PaperSizes.Names);
        Assert.True(PaperSizes.TryMillimetres("A4", out var a4Width, out var a4Height));
        Assert.Equal(210m, a4Width);
        Assert.Equal(297m, a4Height);
        Assert.True(PaperSizes.TryMillimetres("letter", out var letterWidth, out var letterHeight));
        Assert.Equal(215.9m, letterWidth);
        Assert.Equal(279.4m, letterHeight);
        Assert.False(PaperSizes.TryMillimetres("A5", out _, out _));
        Assert.False(PaperSizes.TryMillimetres(null, out _, out _));
    }

    [Theory]
    [InlineData("portrait", PageOrientation.Portrait)]
    [InlineData("PORTRAIT", PageOrientation.Portrait)]
    [InlineData("Landscape", PageOrientation.Landscape)]
    public void TheOrientationsAreParsedWithoutCase(string text, PageOrientation expected)
    {
        Assert.True(PageOrientations.TryParse(text, out var orientation));
        Assert.Equal(expected, orientation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sideways")]
    [InlineData(null)]
    public void AnUnknownOrientationIsNotParsed(string? text)
    {
        Assert.False(PageOrientations.TryParse(text, out _));
    }

    private static string Margins(decimal top, decimal right, decimal bottom, decimal left) =>
        $$"""{"top":{{Number(top)}},"right":{{Number(right)}},"bottom":{{Number(bottom)}},"left":{{Number(left)}}}""";

    private static string MarginsWithout(string side) =>
        side switch
        {
            "top" => """{"right":12,"bottom":18,"left":12}""",
            "right" => """{"top":15,"bottom":18,"left":12}""",
            "bottom" => """{"top":15,"right":12,"left":12}""",
            _ => """{"top":15,"right":12,"bottom":18}""",
        };

    private static string MarginsWith(string side, string value)
    {
        var members = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["top"] = "15",
            ["right"] = "12",
            ["bottom"] = "18",
            ["left"] = "12",
        };
        members[side] = value;
        return "{" + string.Join(',', members.Select(member => $"\"{member.Key}\":{member.Value}")) + "}";
    }

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static JsonElement Pdf(string paper, string orientation, string margins) =>
        Pdf(objectText: $$"""{"paper":"{{paper}}","orientation":"{{orientation}}","marginMm":{{margins}}}""");

    private static JsonElement Pdf(string objectText)
    {
        using var document = JsonDocument.Parse(objectText);
        return document.RootElement.Clone();
    }
}
