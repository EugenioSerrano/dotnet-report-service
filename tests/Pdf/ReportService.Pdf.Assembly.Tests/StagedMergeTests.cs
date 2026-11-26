using ReportService.Pdf.Assembly;

namespace ReportService.Pdf.Assembly.Tests;

/// <summary>
/// The staged merge: parts are taken in manifest order, in batches of consecutive
/// parts, and the assembled document holds exactly the sum of the parts' pages —
/// no page lost, none duplicated, none added.
/// </summary>
public sealed class StagedMergeTests
{
    [Fact]
    public async Task Every_page_of_every_part_is_assembled_once()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[]
            {
                AssemblyFixture.CreatePart(directory, 0, 3),
                AssemblyFixture.CreatePart(directory, 1, 5),
                AssemblyFixture.CreatePart(directory, 2, 4),
            };
            var output = Path.Combine(directory, "assembled.pdf");

            var result = await AssemblyFixture.AssembleAsync(parts, directory, output);

            Assert.Equal(12, result.PageCount);
            var texts = AssemblyFixture.PageTexts(output);
            Assert.Equal(12, texts.Count);
            Assert.All(texts, text => Assert.Contains("part", text, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Batch_boundaries_keep_every_page()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = Enumerable.Range(0, 7).Select(index => AssemblyFixture.CreatePart(directory, index, index + 1)).ToArray();
            var output = Path.Combine(directory, "assembled.pdf");

            var result = await AssemblyFixture.AssembleAsync(parts, directory, output, batchSize: 3);

            Assert.Equal(28, result.PageCount);
            Assert.Equal(3, result.Metrics.Batches);
            Assert.Equal(28, AssemblyFixture.PageTexts(output).Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task The_staging_folder_is_removed_after_success()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 2) };
            var output = Path.Combine(directory, "assembled.pdf");

            await AssemblyFixture.AssembleAsync(parts, directory, output);

            Assert.False(Directory.Exists(Path.Combine(directory, "staging")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
