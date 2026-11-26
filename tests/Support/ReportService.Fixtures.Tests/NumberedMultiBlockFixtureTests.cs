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
        var number = read.Manifest!.Outputs?.Pdf?.Numbering;
        Assert.NotNull(number);
        Assert.Equal(NumberedMultiBlockFixture.AreaLeftMm, number.Area!.LeftMm);
        Assert.Equal(NumberedMultiBlockFixture.AreaTopMm, number.Area.TopMm);
        Assert.Equal(NumberedMultiBlockFixture.AreaWidthMm, number.Area.WidthMm);
        Assert.Equal(NumberedMultiBlockFixture.AreaHeightMm, number.Area.HeightMm);
        Assert.Equal("Liberation Sans", number.Font!.Family);
        Assert.Equal(NumberedMultiBlockFixture.FontPath, number.Font.File);
        Assert.Equal(9m, number.Font.SizePt);
        Assert.Equal(NumberedMultiBlockFixture.Format, number.Format);
        Assert.Equal(NumberedMultiBlockFixture.Culture, number.Culture);

        using var stored = JsonDocument.Parse(read.CanonicalJson!);
        Assert.True(
            stored.RootElement.TryGetProperty("outputs", out var outputs)
            && outputs.TryGetProperty("pdf", out var pdf)
            && pdf.TryGetProperty("numbering", out _),
            "the parsed manifest must carry outputs.pdf.numbering");
    }

    [Fact]
    public void TheInjectedAreaLiesInsideTheBottomMarginBelowTheContentFloor()
    {
        var yaml = File.ReadAllText(SamplePath);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(NumberedMultiBlockFixture.InsertNumberingContract(yaml)));
        var read = new TemplateManifestReader().Read(stream);

        Assert.True(read.Succeeded, read.Reason);
        var pdf = read.Manifest!.Outputs!.Pdf!;
        Assert.Equal("A4", pdf.Paper);
        var area = pdf.Numbering!.Area!;
        var floor = NumberedMultiBlockFixture.A4HeightMm - pdf.MarginMm!.Bottom!.Value;

        // The shipped layout prints rows down to the content floor (A4 minus the bottom
        // margin). The reserved area must start at or below that floor and end on the
        // page, or record rows would occupy it and the merge verifier would refuse the
        // stamp - the defect that made run 261126-1517 red.
        Assert.True(
            area.TopMm >= floor,
            $"the numbering area starts at {area.TopMm} mm, above the content floor of {floor} mm");
        Assert.True(
            area.TopMm + area.HeightMm <= NumberedMultiBlockFixture.A4HeightMm,
            $"the numbering area ends at {area.TopMm + area.HeightMm} mm, past the page");
    }
}
