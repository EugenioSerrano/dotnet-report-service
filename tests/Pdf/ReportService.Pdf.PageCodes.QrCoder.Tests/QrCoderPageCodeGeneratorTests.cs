using System.Text;
using ReportService.Pdf.Assembly.PageCodes;
using ReportService.Pdf.Verification.PageCodes;

namespace ReportService.Pdf.PageCodes.QrCoder.Tests;

public sealed class QrCoderPageCodeGeneratorTests
{
    private static readonly PageCodeArea Square = new(70.87, 70.87);
    private static readonly Dictionary<string, string> NoOptions = [];

    [Fact]
    public void TheSameValueDrawsTheSameBytesAndAnotherValueOthers()
    {
        var generator = new QrCoderPageCodeGenerator();

        var first = generator.Generate("1/3|INV-00000001", NoOptions, Square).Content!;
        var again = generator.Generate("1/3|INV-00000001", NoOptions, Square).Content!;
        var other = generator.Generate("2/3|INV-00000002", NoOptions, Square).Content!;

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.Null(PageCodeContentValidator.Problem(first));
        Assert.Equal("qrcoder-qr", generator.Name);
    }

    [Fact]
    public void EveryValueOfADeclarationHasTheSameModuleCount()
    {
        var generator = new QrCoderPageCodeGenerator();

        var shortest = Encoding.ASCII.GetString(generator.Generate("1", NoOptions, Square).Content!);
        var longest = Encoding.ASCII.GetString(generator.Generate(new string('9', 120), NoOptions, Square).Content!);

        // The scale is the area over the fixed module count, so it is the same for both.
        Assert.Equal(Scale(shortest), Scale(longest));
    }

    [Theory]
    [InlineData("L")]
    [InlineData("M")]
    [InlineData("Q")]
    [InlineData("H")]
    public void TheWorstCaseAtEveryLevelFitsTheContentAndStoredBounds(string level)
    {
        var check = new QrCoderPageCodeGenerator().Validate(
            new Dictionary<string, string> { ["errorCorrection"] = level },
            Square,
            PageCodeSource.MaximumValueBytes);

        Assert.True(check.Accepted);
        Assert.Null(PageCodeContentValidator.Problem(check.WorstCaseContent));
        Assert.True(PageCodeFormWriter.StoredSize(check.WorstCaseContent!, Square) <= PageCodeContentValidator.MaximumStoredBytes);
        var generated = new QrCoderPageCodeGenerator().Generate(
            new string('Z', PageCodeSource.MaximumValueBytes),
            new Dictionary<string, string> { ["errorCorrection"] = level },
            Square).Content!;
        Assert.True(generated.Length <= check.WorstCaseContent!.Length);
    }

    [Fact]
    public void ANonSquareAreaABadOptionAndAnUnholdableValueAreRefused()
    {
        var generator = new QrCoderPageCodeGenerator();

        Assert.Contains("square", generator.Validate(NoOptions, new PageCodeArea(70, 40), 128).Problem, StringComparison.Ordinal);
        Assert.False(generator.Validate(new Dictionary<string, string> { ["errorCorrection"] = "X" }, Square, 128).Accepted);
        Assert.False(generator.Validate(new Dictionary<string, string> { ["size"] = "1" }, Square, 128).Accepted);
        Assert.False(generator.Validate(NoOptions, Square, 5000).Accepted);
        Assert.NotNull(generator.Generate("x", new Dictionary<string, string> { ["errorCorrection"] = "X" }, Square).Refusal);
        Assert.NotNull(generator.Generate(new string('x', 400), NoOptions, Square).Refusal);
    }

    [Fact]
    public void TheExampleReferencesNoImagingAssembly()
    {
        var references = typeof(QrCoderPageCodeGenerator).Assembly.GetReferencedAssemblies().Select(name => name.Name);

        Assert.DoesNotContain(references, name => name!.StartsWith("System.Drawing", StringComparison.Ordinal));
    }

    private static string Scale(string content) =>
        content.Split('\n').First(line => line.EndsWith(" cm", StringComparison.Ordinal));
}
