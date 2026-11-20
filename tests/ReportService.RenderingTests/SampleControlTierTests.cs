using ReportService.Testing;

namespace ReportService.RenderingTests;

/// <summary>
/// The take's own <c>sample</c> verb at the rendering tier: the takeable family's <c>1.0.0</c>
/// rendered, printed and verified in the product image - the composed HTML's SHA-256 emitted
/// with the figures - and the two known-bad sample controls §8.1 names, each a mutation of
/// that same version: a row part that prints no text, and a header anchor the document parts
/// do not print. The check refuses each with the reason the author reads.
/// </summary>
[Collection(RenderingCollection.Name)]
[Trait("Tier", "engines")]
public sealed class SampleControlTierTests(RenderingSuite suite) : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("sample-controls-").FullName;

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder is not a test failure.
        }
    }

    [Fact]
    public async Task TheTakeableSampleRendersAndCarriesTheComposedHtmlHash()
    {
        var folder = CopyPackage("1.0.0", "takeable");

        var result = await suite.SampleAsync(folder, "takeable");

        Assert.Equal("rendered", result.Outcome);
        Assert.Equal(1, result.Pages);
        Assert.True(result.HtmlBytes > 0);
        Assert.NotNull(result.HtmlSha256);
        Assert.Equal(64, result.HtmlSha256!.Length);
        Assert.All(result.HtmlSha256, character => Assert.True(Uri.IsHexDigit(character), $"'{character}' is not hexadecimal"));
        Assert.Null(result.FailureKind);
    }

    [Fact]
    public async Task ARowThatPrintsNoTextIsRefusedWithTheRowReason()
    {
        var folder = CopyPackage("1.0.0", "row-without-text");
        File.WriteAllText(Path.Combine(folder, "html", "row.html.sbn"), "<tr><td></td></tr>");

        var result = await suite.SampleAsync(folder, "row-without-text");

        Assert.Equal("refused", result.Outcome);
        Assert.Equal("Verification", result.FailureKind);
        Assert.Equal("row-without-text", result.FailureReason);
        Assert.Equal("html/row.html.sbn", result.FailurePart);
    }

    [Fact]
    public async Task AnAnchorTheTemplateDoesNotPrintIsRefusedWithTheAnchorReason()
    {
        var folder = CopyPackage("1.0.0", "anchor-mismatch");
        var declaration = File.ReadAllText(Path.Combine(folder, "template.yaml")).Replace(
            "header: [\"$.reportTitle\", \"$.customer.name\", \"$.currency\", \"$.totals.recordCount\"]",
            "header: [\"$.reportTitle\", \"$.customer.name\", \"$.currency\", \"$.totals.recordCount\", \"$.customer.id\"]",
            StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(folder, "template.yaml"), declaration);

        var result = await suite.SampleAsync(folder, "anchor-mismatch");

        Assert.Equal("refused", result.Outcome);
        Assert.Equal("Verification", result.FailureKind);
        Assert.Equal("anchor-mismatch", result.FailureReason);
    }

    private string CopyPackage(string version, string name)
    {
        var source = Path.Combine(RenderingSuite.SampleGateDirectory, version);
        var target = Path.Combine(root, name);
        CopyFolder(source, target);
        return target;
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var folder in Directory.GetDirectories(source))
        {
            CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)));
        }
    }
}
