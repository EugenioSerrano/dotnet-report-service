using System.Globalization;
using System.Text.Json.Nodes;
using ReportService.Pdf.Assembly;
using ReportService.TemplateChecks;
using ReportService.TemplateChecks.PdfConstraints;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.Tests.PdfConstraints;

/// <summary>
/// The global page-numbering contract at take: a valid contract is admitted, a
/// package that declares none is still taken, and a contract whose area, font,
/// size, wording, culture or fit is unusable is refused TEMPLATE_INVALID. A
/// package that declares page totals must keep the numbering area out of the
/// accounting footer's band. The stamp is measured at the page capacity the check is
/// given - the deployment's configured one - above, at and below 10 000 pages.
/// </summary>
public sealed class NumberingContractCheckTests
{
    private const string FontPath = "fonts/LiberationSans-Regular.ttf";

    [Fact]
    public async Task AValidContractIsAdmitted()
    {
        using var package = Package();

        var result = await CheckAsync(package, Contract());

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task APackageThatDeclaresNoContractIsStillTaken()
    {
        using var package = Package();

        var result = await CheckAsync(package, numbering: null);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AnAreaOutsideThePageIsRefused()
    {
        using var package = Package();

        var result = await CheckAsync(package, Contract(area: Area(left: 150, top: 268, width: 80, height: 8)));

        AssertRefused(result, "outputs.pdf.numbering.area");
    }

    [Fact]
    public async Task ANonPositiveDimensionIsRefused()
    {
        using var package = Package();

        var result = await CheckAsync(package, Contract(area: Area(left: 120, top: 268, width: 0, height: 8)));

        AssertRefused(result, "outputs.pdf.numbering.area");
    }

    [Fact]
    public async Task AMissingFontFileIsRefused()
    {
        using var package = Package(withFont: false);

        var result = await CheckAsync(package, Contract());

        AssertRefused(result, "outputs.pdf.numbering.font.file");
    }

    [Fact]
    public async Task AnEmptyFamilyIsRefused()
    {
        using var package = Package();

        var result = await CheckAsync(package, Contract(family: " "));

        AssertRefused(result, "outputs.pdf.numbering.font");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(73)]
    public async Task ASizeOutsideTheRangeIsRefused(int size)
    {
        using var package = Package();

        var result = await CheckAsync(package, Contract(sizePt: size));

        AssertRefused(result, "outputs.pdf.numbering.font.sizePt");
    }

    [Theory]
    [InlineData("Page {X} of {X}")]
    [InlineData("Page {X}")]
    [InlineData("Page {X} of {Y} {Z}")]
    [InlineData("Page X of Y")]
    public async Task AMalformedWordingIsRefused(string format)
    {
        using var package = Package();

        var result = await CheckAsync(package, Contract(format: format));

        AssertRefused(result, "outputs.pdf.numbering.format");
    }

    [Fact]
    public async Task AnInvalidCultureIsRefused()
    {
        using var package = Package();

        var result = await CheckAsync(package, Contract(culture: "not a culture"));

        AssertRefused(result, "outputs.pdf.numbering.culture");
    }

    [Fact]
    public async Task AWordingThatDoesNotFitAtTheCeilingIsRefused()
    {
        using var package = Package();

        var result = await CheckAsync(
            package,
            Contract(area: Area(left: 120, top: 268, width: 12, height: 8)));

        AssertRefused(result, "does not fit");
    }

    [Fact]
    public async Task ACapacityOfOneHundredThousandRefusesAnAreaThatFitsOnlyTenThousand()
    {
        using var package = Package();
        var widthMm = MidpointMm("Page 10000 of 10000", "Page 100000 of 100000");

        var result = await CheckAsync(package, Contract(area: Area(width: widthMm)), capacity: 100_000);

        AssertRefused(result, "does not fit");
    }

    [Fact]
    public async Task ACapacityBelowTenThousandAdmitsAnAreaThatFitsItsOwnWidestStamp()
    {
        using var package = Package();
        var widthMm = MidpointMm("Page 999 of 999", "Page 10000 of 10000");

        var atItsCapacity = await CheckAsync(package, Contract(area: Area(width: widthMm)), capacity: 999);
        var atTenThousand = await CheckAsync(package, Contract(area: Area(width: widthMm)), capacity: 10_000);

        Assert.True(atItsCapacity.Passed, atItsCapacity.Reason);
        AssertRefused(atTenThousand, "does not fit");
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(999)]
    [InlineData(int.MaxValue)]
    public async Task TheRefusalNamesTheCapacityMeasured(int capacity)
    {
        using var package = Package();

        var result = await CheckAsync(
            package,
            Contract(area: Area(left: 120, top: 268, width: 2, height: 8)),
            capacity: capacity);

        AssertRefused(
            result,
            "at the deployment's document capacity of " + capacity.ToString(CultureInfo.InvariantCulture) + " pages.");
        Assert.DoesNotContain("10000 pages", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ACapacityThatIsNotPositiveIsRejected(int pages) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new NumberingCapacity(pages));

    [Fact]
    public void TheCheckRequiresACapacity()
    {
        using var session = new TemplateCheckSession(TimeProvider.System);

        Assert.Throws<ArgumentNullException>(() => new NumberingContractCheck(session, null!));
    }

    [Fact]
    public async Task PageTotalsKeepTheAreaOutOfTheFooterBand()
    {
        using var package = Package();

        var result = await CheckAsync(
            package,
            Contract(area: Area(left: 120, top: 275, width: 80, height: 10)),
            pageTotals: true);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.Contains("footer", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAreaAboveTheFooterBandIsAdmittedWhenTotalsAreDeclared()
    {
        using var package = Package();

        var result = await CheckAsync(
            package,
            Contract(area: Area(left: 120, top: 250, width: 80, height: 8)),
            pageTotals: true);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public void TheCheckIsNamedAndCarriesItsRuleIdentifier()
    {
        using var session = new TemplateCheckSession(TimeProvider.System);
        var check = new NumberingContractCheck(session, new NumberingCapacity(10_000));

        Assert.Equal("NumberingContractCheck", check.Name);
        Assert.Equal(TemplateRefusal.Rules.NumberingContract, NumberingContractCheck.RuleId);
    }

    private static async Task<TemplateCheckResult> CheckAsync(
        TempPackage package,
        JsonNode? numbering,
        bool pageTotals = false,
        int capacity = 10_000)
    {
        var manifest = Packages.ManifestJson();
        var root = JsonNode.Parse(manifest)!;
        var pdf = root["outputs"]!["pdf"]!.AsObject();
        if (numbering is not null)
        {
            pdf["numbering"] = numbering;
        }

        if (pageTotals)
        {
            pdf["pageTotals"] = new JsonObject { ["enabled"] = true };
        }

        using var session = new TemplateCheckSession(TimeProvider.System);
        return await new NumberingContractCheck(session, new NumberingCapacity(capacity))
            .CheckAsync(package.Candidate(root.ToJsonString()), TestContext.Current.CancellationToken);
    }

    private static void AssertRefused(TemplateCheckResult result, string expected)
    {
        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.NotNull(result.Reason);
        Assert.StartsWith("template.yaml:", result.Reason, StringComparison.Ordinal);
        Assert.Contains(expected, result.Reason, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.Reason) <= TemplateRefusal.MaximumBytes);
    }

    /// <summary>The area width, in millimetres, halfway between two stamps' measured widths.</summary>
    private static double MidpointMm(string narrower, string wider)
    {
        var font = new NumberingFont(
            "Liberation Sans",
            FontPath,
            9,
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf")));
        var probe = new PageNumbering(new NumberingArea(120, 268, 80, 8), font, "Page {X} of {Y}", "en-US");
        var low = NumberingFit.Measure(probe, narrower).WidthPt;
        var high = NumberingFit.Measure(probe, wider).WidthPt;
        Assert.True(low < high);
        return Math.Round((low + high) / 2 / NumberingArea.PointsPerMillimetre, 3);
    }

    private static TempPackage Package(bool withFont = true)
    {
        var package = Packages.Package();
        if (withFont)
        {
            var directory = Path.Combine(package.Directory, "fonts");
            Directory.CreateDirectory(directory);
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf"),
                Path.Combine(package.Directory, FontPath.Replace('/', Path.DirectorySeparatorChar)),
                overwrite: true);
        }

        return package;
    }

    private static JsonObject Contract(
        JsonObject? area = null,
        string family = "Liberation Sans",
        int sizePt = 9,
        string format = "Page {X} of {Y}",
        string culture = "en-US") =>
        new()
        {
            ["area"] = area ?? Area(),
            ["font"] = new JsonObject
            {
                ["family"] = family,
                ["file"] = FontPath,
                ["sizePt"] = sizePt,
            },
            ["format"] = format,
            ["culture"] = culture,
        };

    private static JsonObject Area(double left = 120, double top = 268, double width = 80, double height = 8) =>
        new()
        {
            ["leftMm"] = left,
            ["topMm"] = top,
            ["widthMm"] = width,
            ["heightMm"] = height,
        };
}
