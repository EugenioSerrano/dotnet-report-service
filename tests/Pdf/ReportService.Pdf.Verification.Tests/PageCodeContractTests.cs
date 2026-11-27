using System.Text;
using ReportService.Pdf.Verification.PageCodes;

namespace ReportService.Pdf.Verification.Tests;

public sealed class PageCodeContractTests
{
    private static readonly PageCodeArea Area = new(56.69, 56.69);

    [Theory]
    [InlineData("q\n0 g\n1 2 3 4 re\nf\nQ\n")]
    [InlineData("q 1 0 0 1 2 3 cm 0.5 w 0 J 0 j 4 M [1 2] 0 d 1 1 m 2 2 l 1 2 3 4 5 6 c 1 2 3 4 v 1 2 3 4 y h S Q")]
    [InlineData("q 0 0 1 rg 0 0 1 RG 0 0 0 1 k 0 0 0 1 K 0 G 1 1 2 2 re W n 1 1 2 2 re f* F B B* b b* s Q")]
    [InlineData("% a comment\nq -1.5 +2 .5 3. re f Q")]
    public void AllowedContentIsAccepted(string content) =>
        Assert.Null(PageCodeContentValidator.Problem(Encoding.ASCII.GetBytes(content)));

    [Theory]
    [InlineData("", "empty")]
    [InlineData("BT (x) Tj ET", "not allowed")]
    [InlineData("/Im0 Do", "not allowed")]
    [InlineData("BI ID EI", "not allowed")]
    [InlineData("/GS0 gs", "not allowed")]
    [InlineData("q q Q", "does not restore")]
    [InlineData("Q", "never saved")]
    [InlineData("q Q 1 2", "ends with operands")]
    [InlineData("q 1.2.3 w Q", "not allowed")]
    [InlineData("q 1- w Q", "not allowed")]
    public void DisallowedContentIsRefused(string content, string reason) =>
        Assert.Contains(reason, PageCodeContentValidator.Problem(Encoding.ASCII.GetBytes(content)), StringComparison.Ordinal);

    [Fact]
    public void NonAsciiAndOversizedContentIsRefused()
    {
        Assert.Contains("ASCII", PageCodeContentValidator.Problem([(byte)'q', 0xC3, 0xA9, (byte)'Q']), StringComparison.Ordinal);
        Assert.Contains("ASCII", PageCodeContentValidator.Problem([(byte)'q', 0x01, (byte)'Q']), StringComparison.Ordinal);
        var large = Encoding.ASCII.GetBytes("q " + string.Concat(Enumerable.Repeat("1 1 1 1 re ", 7000)) + "f Q");
        Assert.Contains("larger", PageCodeContentValidator.Problem(large), StringComparison.Ordinal);
    }

    [Fact]
    public void TheReferenceGridIsDeterministicDistinctPerValueAndOfOneLength()
    {
        var generator = new ReferenceGridPageCodeGenerator();
        var none = new Dictionary<string, string>();

        var first = generator.Generate("1/3|INV-1", none, Area).Content!;
        var again = generator.Generate("1/3|INV-1", none, Area).Content!;
        var other = generator.Generate("2/3|INV-2", none, Area).Content!;
        var check = generator.Validate(none, Area, PageCodeSource.MaximumValueBytes);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.Equal(first.Length, other.Length);
        Assert.Equal(first.Length, check.WorstCaseContent!.Length);
        Assert.Null(PageCodeContentValidator.Problem(first));
        Assert.Equal(ReferenceGridPageCodeGenerator.GeneratorName, generator.Name);
        Assert.False(generator.Validate(new Dictionary<string, string> { ["x"] = "y" }, Area, 128).Accepted);
    }

    [Fact]
    public void AModuleMatrixDrawsOneRectanglePerDarkRunAndItsWorstCaseIsLarger()
    {
        var grid = new bool[,] { { true, true, false }, { false, false, false }, { true, false, true } };

        var content = Encoding.ASCII.GetString(ModuleMatrixContent.Draw(grid, new PageCodeArea(30, 30)));

        Assert.Equal(3, content.Split(" re").Length - 1);
        Assert.StartsWith("q\n10 0 0 10 0 0 cm\n0 g\n", content, StringComparison.Ordinal);
        Assert.Contains("0 2 2 1 re", content, StringComparison.Ordinal);
        Assert.True(ModuleMatrixContent.WorstCase(3, new PageCodeArea(30, 30)).Length > content.Length);
        Assert.Null(PageCodeContentValidator.Problem(ModuleMatrixContent.WorstCase(41, Area)));
    }

    [Fact]
    public void ChecksAndContentsCarryTheirOutcome()
    {
        var refused = PageCodeCheck.Refuse("problem", "fix");
        var content = PageCodeContent.Refused("too long");

        Assert.False(refused.Accepted);
        Assert.Equal("problem", refused.Problem);
        Assert.Equal("fix", refused.Fix);
        Assert.Null(content.Content);
        Assert.Equal("too long", content.Refusal);
        Assert.Throws<ArgumentException>(() => PageCodeCheck.Refuse(" ", "fix"));
        Assert.Throws<ArgumentNullException>(() => PageCodeContent.Of(null!));
    }

    [Fact]
    public void TheRegistryResolvesByNameAndRefusesADuplicate()
    {
        var grid = new ReferenceGridPageCodeGenerator();
        var registered = new RegisteredPageCodeGenerator(grid, PageCodeGeneratorDescriptor.Of(grid, PageCodeGeneratorOrigins.Reference));

        var registry = new PageCodeGeneratorRegistry([registered]);

        Assert.True(registry.TryResolve("reference-grid", out var found));
        Assert.Same(grid, found.Generator);
        Assert.False(registry.TryResolve("acme", out _));
        Assert.Equal(["reference-grid"], registry.Names);
        var duplicate = Assert.Throws<DuplicatePageCodeGeneratorException>(() => new PageCodeGeneratorRegistry([registered, registered]));
        Assert.Equal("reference-grid", duplicate.GeneratorName);
    }

    [Fact]
    public void TheDescriptorNamesTheGeneratorsAssemblyAndVersion()
    {
        var descriptor = PageCodeGeneratorDescriptor.Of(new ReferenceGridPageCodeGenerator(), PageCodeGeneratorOrigins.Configured, "/opt/codes/x.dll");

        Assert.Equal("reference-grid", descriptor.Name);
        Assert.Equal("ReportService.Pdf.Verification", descriptor.AssemblyName);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.AssemblyVersion));
        Assert.Equal("configured", descriptor.Origin);
        Assert.Equal("/opt/codes/x.dll", descriptor.Path);
    }

    [Fact]
    public void AValidDeclarationIsCreatedWithItsAreaInPoints()
    {
        var problems = new List<PageCodeProblem>();
        var declaration = PageCodeDeclaration.Create(Raw(), problems);

        Assert.Empty(problems);
        Assert.NotNull(declaration);
        Assert.Equal(170 * 72 / 25.4, declaration.Area.Region.LeftPt, 6);
        Assert.Equal(25 * 72 / 25.4, declaration.Area.Size.HeightPt, 6);
        Assert.Empty(declaration.Options);
    }

    [Theory]
    [InlineData("Acme_QR", "generator")]
    [InlineData(null, "generator")]
    public void AnInvalidGeneratorNameIsRefused(string? name, string field) =>
        Assert.Contains(Problems(Raw() with { Generator = name }), problem => problem.Field == field);

    [Fact]
    public void AnAreaOutOfRangeIsRefused()
    {
        Assert.Contains(Problems(Raw() with { WidthMm = 4 }), problem => problem.Field == "area");
        Assert.Contains(Problems(Raw() with { HeightMm = 101 }), problem => problem.Field == "area");
        Assert.Contains(Problems(Raw() with { LeftMm = -1 }), problem => problem.Field == "area");
        Assert.Contains(Problems(Raw() with { TopMm = null }), problem => problem.Field == "area");
    }

    [Fact]
    public void TooManyOrMalformedOptionsAreRefused()
    {
        var many = Enumerable.Range(0, 9).ToDictionary(index => "k" + index, _ => "v");
        Assert.Contains(Problems(Raw() with { Options = many }), problem => problem.Field == "options");
        Assert.Contains(Problems(Raw() with { Options = new Dictionary<string, string> { ["bad key"] = "v" } }), problem => problem.Field == "options");
        Assert.Contains(Problems(Raw() with { Options = new Dictionary<string, string> { ["k"] = new string('v', 65) } }), problem => problem.Field == "options");
    }

    [Fact]
    public void ASourceProblemRefusesTheDeclaration() =>
        Assert.Contains(Problems(Raw() with { Value = "{nope}" }), problem => problem.Field == "source.value");

    private static List<PageCodeProblem> Problems(PageCodeDeclaration.Raw raw)
    {
        var problems = new List<PageCodeProblem>();
        Assert.Null(PageCodeDeclaration.Create(raw, problems));
        return problems;
    }

    private static PageCodeDeclaration.Raw Raw() =>
        new("acme-qr", null, 170, 10, 25, 25, "{page.number}|{match}", "INV-[0-9]{8}", 12);
}
