using ReportService.Templates;
using ReportService.Testing;

namespace ReportService.PerformanceTests;

/// <summary>
/// The guarded writer's large-buffer guards, kept out of the hermetic inner loop
/// because each case allocates nine-mebibyte blocks to drive the thirty-two
/// mebibyte block bound across calls. They are relocated unchanged: the nine
/// mebibyte block is what crosses the bound, so it is not reduced. The writer's
/// single-call character guard and the per-call reset stay covered hermetically
/// by the small cases that remain in the writer's own test project.
/// </summary>
[Collection(nameof(MemoryMeasured))]
public sealed class GuardedWriterLargeBufferTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ABlockAboveThirtyTwoMebibytesIsRefusedEvenAcrossCalls()
    {
        var clock = new ManualTimeProvider(Now);
        var writer = new GuardedWriter(new DiscardingWriter(), clock);
        var chunk = new string('a', 9 * 1024 * 1024);

        var refusal = Assert.Throws<TemplateGuardException>(() =>
        {
            for (var call = 0; call < 4; call++)
            {
                writer.BeginCall();
                writer.Write(chunk);
            }
        });

        Assert.Equal(TemplateErrorCodes.QuotaExceeded, refusal.Code);
        Assert.True(writer.BytesWritten <= GuardedWriter.MaximumBlockBytes);
    }

    [Fact]
    public void ANewRenderCallDoesNotInheritTheCharactersOfThePreviousOne()
    {
        var clock = new ManualTimeProvider(Now);
        var writer = new GuardedWriter(new DiscardingWriter(), clock);
        var chunk = new string('a', 9 * 1024 * 1024);

        writer.Write(chunk);
        writer.BeginCall();
        writer.Write(chunk);

        Assert.Equal(chunk.Length, writer.CallCharacters);
    }

    /// <summary>A sink that keeps nothing: a guard case measures the writer, not the text.</summary>
    private sealed class DiscardingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(string? value)
        {
        }
    }
}
