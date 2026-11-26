using ReportService.Pdf.Assembly;

namespace ReportService.Pdf.Assembly.Tests;

/// <summary>
/// Manifest order: the parts may be supplied, written and committed in any order,
/// and the assembled document concatenates them by their block index — never by
/// the order they happened to finish.
/// </summary>
public sealed class OrderedMergeTests
{
    [Fact]
    public async Task Parts_supplied_out_of_order_still_merge_in_manifest_order()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var second = AssemblyFixture.CreatePart(directory, 2, 1);
            var first = AssemblyFixture.CreatePart(directory, 0, 2);
            var third = AssemblyFixture.CreatePart(directory, 1, 1);
            var output = Path.Combine(directory, "assembled.pdf");

            var ordered = new[] { first, third, second }.OrderBy(part => part.PartIndex).ToArray();
            await AssemblyFixture.AssembleAsync(ordered, directory, output);

            var texts = AssemblyFixture.PageTexts(output);
            Assert.Equal(4, texts.Count);
            Assert.Contains("part0page1", texts[0], StringComparison.Ordinal);
            Assert.Contains("part0page2", texts[1], StringComparison.Ordinal);
            Assert.Contains("part1page1", texts[2], StringComparison.Ordinal);
            Assert.Contains("part2page1", texts[3], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task The_offsets_of_each_part_follow_the_manifest()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[]
            {
                AssemblyFixture.CreatePart(directory, 0, 4),
                AssemblyFixture.CreatePart(directory, 1, 6),
                AssemblyFixture.CreatePart(directory, 2, 2),
            };
            var output = Path.Combine(directory, "assembled.pdf");

            await AssemblyFixture.AssembleAsync(parts, directory, output);

            var texts = AssemblyFixture.PageTexts(output);
            for (var page = 1; page <= 4; page++)
            {
                Assert.Contains($"part0page{page}", texts[page - 1], StringComparison.Ordinal);
            }

            for (var page = 1; page <= 6; page++)
            {
                Assert.Contains($"part1page{page}", texts[3 + page], StringComparison.Ordinal);
            }

            for (var page = 1; page <= 2; page++)
            {
                Assert.Contains($"part2page{page}", texts[9 + page], StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
