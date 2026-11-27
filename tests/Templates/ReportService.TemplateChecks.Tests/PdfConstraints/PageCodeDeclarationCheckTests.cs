using System.Text;
using System.Text.Json.Nodes;
using ReportService.Pdf.Verification.PageCodes;
using ReportService.TemplateChecks.PdfConstraints;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.Tests.PdfConstraints;

/// <summary>
/// The per-page code declaration at take: a valid declaration naming a registered
/// generator is admitted; a package without one is untouched; every rule of the
/// declaration, of its area, of the deployment's generator and of the generator's
/// worst case refuses TEMPLATE_INVALID naming the manifest and the fix.
/// </summary>
public sealed class PageCodeDeclarationCheckTests
{
    [Fact]
    public async Task AValidDeclarationOnARegisteredGeneratorIsAdmitted()
    {
        var result = await CheckAsync(Declaration());

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task APackageWithoutADeclarationPasses()
    {
        var result = await CheckAsync(pageCode: null);

        Assert.True(result.Passed, result.Reason);
    }

    [Theory]
    [InlineData("generator", "Bad Name", "outputs.pdf.pageCode.generator")]
    [InlineData("value", "{unknown}", "outputs.pdf.pageCode.source.value")]
    [InlineData("pattern", "(", "outputs.pdf.pageCode.source.match.pattern")]
    public async Task AMalformedDeclarationIsRefusedNamingItsField(string field, string value, string expected)
    {
        var declaration = Declaration();
        switch (field)
        {
            case "generator":
                declaration["generator"] = value;
                break;
            case "value":
                declaration["source"]!["value"] = value;
                break;
            default:
                declaration["source"]!["match"]!["pattern"] = value;
                break;
        }

        AssertRefused(await CheckAsync(declaration), expected);
    }

    [Fact]
    public async Task AnAreaTooSmallIsRefused() =>
        AssertRefused(await CheckAsync(Declaration(area: Area(150, 20, 3, 3))), "outputs.pdf.pageCode.area");

    [Fact]
    public async Task AnAreaOutsideThePrintableBoxIsRefused() =>
        AssertRefused(await CheckAsync(Declaration(area: Area(190, 20, 15, 15))), "printable box");

    [Fact]
    public async Task AnAreaOverTheNumberingAreaIsRefused() =>
        AssertRefused(
            await CheckAsync(Declaration(area: Area(150, 255, 20, 20)), numbering: Area(120, 268, 80, 8)),
            "overlaps outputs.pdf.numbering.area");

    [Fact]
    public async Task AnAreaOverTheAccountingFooterIsRefused() =>
        AssertRefused(
            await CheckAsync(Declaration(area: Area(150, 250, 20, 20)), footer: Area(12, 255, 186, 10)),
            "accounting footer");

    [Fact]
    public async Task AnUnregisteredGeneratorIsRefusedNamingTheRegisteredOnes()
    {
        var declaration = Declaration();
        declaration["generator"] = "acme-qr";

        var result = await CheckAsync(declaration);

        AssertRefused(result, "no page-code generator of that name is registered");
        Assert.Contains("reference-grid", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoGeneratorRegisteredTheFixSaysToRegisterOne()
    {
        var result = await CheckAsync(Declaration(), registry: new PageCodeGeneratorRegistry([]));

        AssertRefused(result, "no page-code generator of that name is registered");
        Assert.Contains("PageCodes", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGeneratorsOwnRefusalIsReported()
    {
        var declaration = Declaration();
        declaration["options"] = new JsonObject { ["size"] = "large" };

        AssertRefused(await CheckAsync(declaration), "refuses the declaration");
    }

    [Fact]
    public async Task AWorstCaseOverTheContentBoundIsRefused()
    {
        var huge = Encoding.ASCII.GetBytes("q " + string.Concat(Enumerable.Repeat("1 1 1 1 re ", 7000)) + "f Q");

        AssertRefused(await CheckAsync(Declaration("huge"), registry: Registry(new Fixed("huge", huge))), "does not accept");
    }

    [Fact]
    public async Task AWorstCaseOverTheStoredBoundIsRefused()
    {
        var random = new Random(3);
        var builder = new StringBuilder("q ");
        while (builder.Length < 60_000)
        {
            builder.Append(random.Next(100000, 999999)).Append(' ').Append(random.Next(100000, 999999)).Append(" 1 1 re ");
        }

        var noisy = Encoding.ASCII.GetBytes(builder.Append("f Q").ToString());

        AssertRefused(await CheckAsync(Declaration("noisy"), registry: Registry(new Fixed("noisy", noisy))), "as stored");
    }

    [Fact]
    public async Task AValueBoundOverTheLimitIsRefusedWithTheLargestMatchLength()
    {
        var declaration = Declaration();
        declaration["source"]!["match"]!["maxLength"] = 64;

        var result = await CheckAsync(declaration);

        AssertRefused(result, "can produce a value of");
        Assert.Contains("maxLength of at most", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALongLiteralTemplateIsRefusedWithoutAMatchHint()
    {
        var declaration = Declaration();
        declaration["source"] = new JsonObject { ["value"] = new string('x', 200) };

        var result = await CheckAsync(declaration);

        AssertRefused(result, "outputs.pdf.pageCode.source.value can produce");
    }

    [Fact]
    public void TheCheckIsNamedForItsPlaceInTheOrder()
    {
        Assert.Equal("PageCodeDeclarationCheck", new PageCodeDeclarationCheck([]).Name);
        Assert.Contains(PageCodeDeclarationCheck.CheckName, TemplateChecksRegistration.Order);
    }

    private static async Task<TemplateCheckResult> CheckAsync(
        JsonObject? pageCode,
        JsonObject? numbering = null,
        JsonObject? footer = null,
        PageCodeGeneratorRegistry? registry = null)
    {
        using var package = Packages.Package();
        var root = JsonNode.Parse(Packages.ManifestJson())!;
        var pdf = root["outputs"]!["pdf"]!.AsObject();
        if (pageCode is not null)
        {
            pdf["pageCode"] = pageCode;
        }

        if (numbering is not null)
        {
            pdf["numbering"] = new JsonObject { ["area"] = numbering };
        }

        if (footer is not null)
        {
            pdf["pageTotals"] = new JsonObject { ["enabled"] = true, ["footer"] = footer };
        }

        return await new PageCodeDeclarationCheck([registry ?? Registry(new ReferenceGridPageCodeGenerator())])
            .CheckAsync(package.Candidate(root.ToJsonString()), TestContext.Current.CancellationToken);
    }

    private static PageCodeGeneratorRegistry Registry(IPageCodeGenerator generator) =>
        new([new RegisteredPageCodeGenerator(generator, PageCodeGeneratorDescriptor.Of(generator, PageCodeGeneratorOrigins.Reference))]);

    private static void AssertRefused(TemplateCheckResult result, string expected)
    {
        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.StartsWith("template.yaml:", result.Reason, StringComparison.Ordinal);
        Assert.Contains(expected, result.Reason, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(result.Reason!) <= TemplateRefusal.MaximumBytes);
    }

    private static JsonObject Declaration(string generator = "reference-grid", JsonObject? area = null) =>
        new()
        {
            ["generator"] = generator,
            ["area"] = area ?? Area(160, 20, 25, 25),
            ["source"] = new JsonObject
            {
                ["value"] = "{template.id}|{page.number}/{page.total}|{match}",
                ["match"] = new JsonObject { ["pattern"] = "INV-[0-9]{8}", ["maxLength"] = 12 },
            },
        };

    private static JsonObject Area(double left, double top, double width, double height) =>
        new() { ["leftMm"] = left, ["topMm"] = top, ["widthMm"] = width, ["heightMm"] = height };

    private sealed class Fixed(string name, byte[] content) : IPageCodeGenerator
    {
        public string Name => name;

        public PageCodeCheck Validate(IReadOnlyDictionary<string, string> options, PageCodeArea area, int maxValueBytes) =>
            PageCodeCheck.Accept(content);

        public PageCodeContent Generate(string value, IReadOnlyDictionary<string, string> options, PageCodeArea area) =>
            PageCodeContent.Of(content);
    }
}
