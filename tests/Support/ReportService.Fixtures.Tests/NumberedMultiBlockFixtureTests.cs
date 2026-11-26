using System.Text;
using System.Text.Json;
using ReportService.TemplateSource.Manifest;
using ReportService.Testing;
using ReportService.Testing.TemplatePackages;

namespace ReportService.Fixtures.Tests;

/// <summary>
/// The multi-block fixture's manifest transformation: the shipped sample's YAML is
/// LF-only on disk, and the injection must carry the numbering contract whether the
/// file uses LF or CRLF. The taken manifest is parsed by the product's own reader and
/// must expose outputs.pdf.numbering with the area, font, wording and culture the
/// merge worker's contract requires; a silent no-op injection fails here, in the
/// fixture, rather than in the covering run.
/// </summary>
public sealed class NumberedMultiBlockFixtureTests
{
    private static string SamplePath => Path.Combine(
        RepositoryPaths.Root,
        "samples",
        "templates",
        NumberedMultiBlockFixture.Sample,
        "template.yaml");

    [Fact]
    public void TheInjectedContractIsPresentWhenTheSampleUsesLf()
    {
        var yaml = File.ReadAllText(SamplePath);
        Assert.DoesNotContain("\r\n", yaml, StringComparison.Ordinal);

        AssertContract(NumberedMultiBlockFixture.InsertNumberingContract(yaml));
    }

    [Fact]
    public void TheInjectedContractIsPresentWhenTheSampleUsesCrLf()
    {
        var yaml = File.ReadAllText(SamplePath).Replace("\n", "\r\n", StringComparison.Ordinal);

        AssertContract(NumberedMultiBlockFixture.InsertNumberingContract(yaml));
    }

    [Fact]
    public void TheInjectionKeepsTheFilesOwnNewline()
    {
        var lf = File.ReadAllText(SamplePath);
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.DoesNotContain("\r\n", NumberedMultiBlockFixture.InsertNumberingContract(lf), StringComparison.Ordinal);
        Assert.Contains("\r\n", NumberedMultiBlockFixture.InsertNumberingContract(crlf), StringComparison.Ordinal);
    }

    private static void AssertContract(string yaml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(yaml));
        var read = new TemplateManifestReader().Read(stream);

        Assert.True(read.Succeeded, read.Reason);
        var numbering = read.Manifest!.Outputs?.Pdf?.Numbering;
        Assert.NotNull(numbering);
        Assert.Equal(120m, numbering.Area!.LeftMm);
        Assert.Equal(268m, numbering.Area.TopMm);
        Assert.Equal(80m, numbering.Area.WidthMm);
        Assert.Equal(8m, numbering.Area.HeightMm);
        Assert.Equal("Liberation Sans", numbering.Font!.Family);
        Assert.Equal(NumberedMultiBlockFixture.FontPath, numbering.Font.File);
        Assert.Equal(9m, numbering.Font.SizePt);
        Assert.Equal(NumberedMultiBlockFixture.Format, numbering.Format);
        Assert.Equal(NumberedMultiBlockFixture.Culture, numbering.Culture);

        using var stored = JsonDocument.Parse(read.CanonicalJson!);
        Assert.True(
            stored.RootElement.TryGetProperty("outputs", out var outputs)
            && outputs.TryGetProperty("pdf", out var pdf)
            && pdf.TryGetProperty("numbering", out _),
            "the parsed manifest must carry outputs.pdf.numbering");
    }
}
