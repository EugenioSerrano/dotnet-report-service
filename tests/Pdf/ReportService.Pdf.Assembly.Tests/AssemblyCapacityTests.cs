using ReportService.Pdf.Assembly;

namespace ReportService.Pdf.Assembly.Tests;

/// <summary>
/// The capacity a caller configures is re-checked before any part is read: a part set
/// exactly at the configured pages or bytes passes the check, one page or one byte more
/// is refused naming the limit, the configured value and the reached value, and a
/// request that configures no capacity is not refused for its size by this library.
/// </summary>
public sealed class AssemblyCapacityTests
{
    [Theory]
    [InlineData(10_001, 10_000)]
    [InlineData(301, 300)]
    [InlineData(2, 1)]
    public async Task A_part_set_one_page_over_the_configured_pages_is_refused_before_reading_a_part(int pages, int configured)
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[]
            {
                new AssemblyPart(0, Path.Combine(directory, "missing.pdf"), pages - 1, 10),
                new AssemblyPart(1, Path.Combine(directory, "missing2.pdf"), 1, 10),
            };

            var refused = await Assert.ThrowsAsync<AssemblyLimitExceededException>(() =>
                AssembleAsync(parts, directory, maximumPages: configured, maximumPartBytes: long.MaxValue));

            Assert.Equal("document-pages", refused.Limit);
            Assert.Equal(configured, refused.LimitValue);
            Assert.Equal(pages, refused.ReachedValue);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(37_748_737L, 37_748_736L)]
    [InlineData(1_001L, 1_000L)]
    public async Task A_part_set_one_byte_over_the_configured_bytes_is_refused_before_reading_a_part(long bytes, long configured)
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[]
            {
                new AssemblyPart(0, Path.Combine(directory, "missing.pdf"), 1, bytes - 1),
                new AssemblyPart(1, Path.Combine(directory, "missing2.pdf"), 1, 1),
            };

            var refused = await Assert.ThrowsAsync<AssemblyLimitExceededException>(() =>
                AssembleAsync(parts, directory, maximumPages: int.MaxValue, maximumPartBytes: configured));

            Assert.Equal("document-bytes", refused.Limit);
            Assert.Equal(configured, refused.LimitValue);
            Assert.Equal(bytes, refused.ReachedValue);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Pages_are_judged_before_bytes_when_one_part_set_passes_both()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[] { new AssemblyPart(0, Path.Combine(directory, "missing.pdf"), 5, 500) };

            var refused = await Assert.ThrowsAsync<AssemblyLimitExceededException>(() =>
                AssembleAsync(parts, directory, maximumPages: 4, maximumPartBytes: 499));

            Assert.Equal("document-pages", refused.Limit);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_document_exactly_at_the_configured_capacity_is_assembled()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[]
            {
                AssemblyFixture.CreatePart(directory, 0, 3),
                AssemblyFixture.CreatePart(directory, 1, 2),
            };
            var bytes = parts.Sum(part => part.SizeBytes);

            var result = await AssembleAsync(parts, directory, maximumPages: 5, maximumPartBytes: bytes);

            Assert.Equal(5, result.PageCount);
            Assert.Equal(5, result.Metrics.CapacityPages);
            Assert.Equal(bytes, result.Metrics.CapacityPartBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_request_that_configures_no_capacity_is_not_refused_for_its_size_here()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 2) };

            var result = await AssemblyFixture.AssembleAsync(parts, directory, Path.Combine(directory, "out.pdf"));

            Assert.Equal(2, result.PageCount);
            Assert.Null(result.Metrics.CapacityPages);
            Assert.Null(result.Metrics.CapacityPartBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task An_empty_part_set_is_refused()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            await Assert.ThrowsAsync<AssemblyInputException>(() =>
                AssembleAsync([], directory, maximumPages: 1, maximumPartBytes: 1));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<AssemblyResult> AssembleAsync(
        IReadOnlyList<AssemblyPart> parts,
        string directory,
        int maximumPages,
        long maximumPartBytes) =>
        new PdfSharpAssembler().AssembleAsync(
            new AssemblyRequest(
                parts,
                AssemblyFixture.Contract(),
                Path.Combine(directory, "out.pdf"),
                directory,
                MaximumPages: maximumPages,
                MaximumPartBytes: maximumPartBytes),
            CancellationToken.None);
}
