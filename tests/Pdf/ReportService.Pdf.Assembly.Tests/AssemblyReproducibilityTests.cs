using ReportService.Pdf.Assembly;

namespace ReportService.Pdf.Assembly.Tests;

/// <summary>
/// Content-level reproducibility: two assemblies of the same parts produce the same
/// text on every page and the same stamps. The comparison is of content, never of
/// raw bytes, because the writer's XMP identifiers and font-subset tags are random
/// by design.
/// </summary>
public sealed class AssemblyReproducibilityTests
{
    [Fact]
    public async Task Two_assemblies_of_the_same_parts_have_the_same_content()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[]
            {
                AssemblyFixture.CreatePart(directory, 0, 2),
                AssemblyFixture.CreatePart(directory, 1, 3),
            };
            var first = Path.Combine(directory, "first.pdf");
            var second = Path.Combine(directory, "second.pdf");

            await AssemblyFixture.AssembleAsync(parts, directory, first);
            await AssemblyFixture.AssembleAsync(parts, directory, second);

            Assert.Equal(AssemblyFixture.PageTexts(first), AssemblyFixture.PageTexts(second));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task The_metrics_record_the_font_hash_and_the_writer_version()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 1) };
            var output = Path.Combine(directory, "assembled.pdf");
            var metrics = new AssemblyMetrics();

            var result = await new PdfSharpAssembler().AssembleAsync(
                new AssemblyRequest(parts, AssemblyFixture.Contract(), output, directory, Metrics: metrics),
                CancellationToken.None);

            Assert.Equal(AssemblyFixture.Font().Sha256, result.Metrics.StampFontSha256);
            Assert.NotEqual("unknown", result.Metrics.PdfLibraryVersion);
            Assert.Equal(1, result.Metrics.Parts);
            Assert.Equal(1, result.Metrics.Pages);
            Assert.True(result.Metrics.OutputBytes > 0);
            Assert.True(result.Metrics.PeakWorkingSetBytes > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
