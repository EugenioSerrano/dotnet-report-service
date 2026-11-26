using ReportService.Pdf.Assembly;

namespace ReportService.Pdf.Assembly.Tests;

/// <summary>
/// The assembly's bounds: the document ceiling is re-checked before anything is
/// imported, batching defaults to 25 consecutive parts, and the temporary disk
/// factor is three times the parts' own bytes.
/// </summary>
public sealed class AssemblyLimitsTests
{
    [Fact]
    public void The_declared_bounds_are_the_architecture_s()
    {
        Assert.Equal(10_000, AssemblyLimits.MaximumPages);
        Assert.Equal(37_748_736L, AssemblyLimits.MaximumPartBytes);
        Assert.Equal(25, AssemblyLimits.BatchSize);
        Assert.Equal(3, AssemblyLimits.TemporaryDiskFactor);
    }

    [Fact]
    public async Task A_part_set_over_the_page_ceiling_is_refused_before_reading_a_part()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[]
            {
                new AssemblyPart(0, Path.Combine(directory, "missing.pdf"), 6_000, 10),
                new AssemblyPart(1, Path.Combine(directory, "missing2.pdf"), 4_001, 10),
            };

            var refused = await Assert.ThrowsAsync<AssemblyLimitExceededException>(() =>
                AssemblyFixture.AssembleAsync(parts, directory, Path.Combine(directory, "out.pdf")));

            Assert.Equal("document-pages", refused.Limit);
            Assert.Equal(10_000, refused.LimitValue);
            Assert.Equal(10_001, refused.ReachedValue);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_part_set_over_the_byte_ceiling_is_refused_before_reading_a_part()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[]
            {
                new AssemblyPart(0, Path.Combine(directory, "missing.pdf"), 1, 20_000_000),
                new AssemblyPart(1, Path.Combine(directory, "missing2.pdf"), 1, 17_748_737),
            };

            var refused = await Assert.ThrowsAsync<AssemblyLimitExceededException>(() =>
                AssemblyFixture.AssembleAsync(parts, directory, Path.Combine(directory, "out.pdf")));

            Assert.Equal("document-bytes", refused.Limit);
            Assert.Equal(37_748_736L, refused.LimitValue);
            Assert.Equal(37_748_737L, refused.ReachedValue);
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
                AssemblyFixture.AssembleAsync([], directory, Path.Combine(directory, "out.pdf")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
