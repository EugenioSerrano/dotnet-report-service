using ReportService.Testing;

namespace ReportService.Templates.Tests;

/// <summary>
/// The caller-side guards: one render call may not produce more than 16 Mi
/// characters, one block may not pass 32 MiB, and a block not produced within 60 s
/// is refused. All three are decided by the writer on its own clock.
/// </summary>
public sealed class GuardedWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheWriterCountsTheBytesOfTheBlock()
    {
        var clock = new ManualTimeProvider(Now);
        var writer = new GuardedWriter(new DiscardingWriter(), clock);

        writer.Write("abc");
        writer.Write("e");

        Assert.Equal(4, writer.BytesWritten);
        Assert.Equal(4, writer.CallCharacters);
    }

    [Fact]
    public void ARenderCallAboveSixteenMillionCharactersIsRefused()
    {
        var clock = new ManualTimeProvider(Now);
        var writer = new GuardedWriter(new DiscardingWriter(), clock);
        var chunk = new string('a', 1024 * 1024);

        var refusal = Assert.Throws<TemplateGuardException>(() =>
        {
            for (var call = 0; call <= GuardedWriter.MaximumCharactersPerCall / chunk.Length; call++)
            {
                writer.Write(chunk);
            }
        });

        Assert.Equal(TemplateErrorCodes.QuotaExceeded, refusal.Code);
        Assert.Equal("render-output", refusal.Limit);
    }

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

    [Fact]
    public void ABlockPastItsDeadlineIsRefusedOnTheNextWrite()
    {
        var clock = new ManualTimeProvider(Now);
        var writer = new GuardedWriter(new DiscardingWriter(), clock);
        writer.Write("before");

        clock.Advance(GuardedWriter.MaximumBlockTime + TimeSpan.FromSeconds(1));
        var refusal = Assert.Throws<TemplateGuardException>(() => writer.Write("after"));

        Assert.Equal(TemplateErrorCodes.RenderTimeout, refusal.Code);
    }

    [Fact]
    public void AnEmptyWriteIsNeitherCountedNorChecked()
    {
        var writer = new GuardedWriter(new DiscardingWriter(), new ManualTimeProvider(Now));

        writer.Write(string.Empty);
        writer.Write((string?)null);

        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public void TheEncodingOfTheWriterIsUtf8()
    {
        var writer = new GuardedWriter(new DiscardingWriter(), new ManualTimeProvider(Now));

        Assert.Equal(System.Text.Encoding.UTF8, writer.Encoding);
        writer.Flush();
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
