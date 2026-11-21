using System.Text;
using ReportService.Testing;

namespace ReportService.Templates.Tests;

/// <summary>
/// The composition call site of the test-only stall: the composer awaits it once, after the
/// guard's clock has started and before the first part is composed, so the first write after
/// it raises the guard's own <c>RENDER_TIMEOUT</c>. With no stall installed the production
/// path composes exactly the bytes it composed before, and the slot is null.
/// </summary>
public sealed class CompositionStallTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 14, 0, 0, TimeSpan.Zero);

    private static readonly PresentationSettings Settings = new(
        System.Globalization.CultureInfo.GetCultureInfo("en-US"),
        TimeZoneInfo.Utc,
        DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task TheFirstWriteAfterTheStallRaisesTheGuardsDeadline()
    {
        var clock = new ManualTimeProvider(Now);
        var composer = new BlockComposer(clock)
        {
            Stall = new AdvancingStall(clock, TimeSpan.FromSeconds(61)),
        };

        var refusal = await Assert.ThrowsAsync<TemplateGuardException>(() =>
            composer.ComposeAsync(Request(), new ScribanTemplateAdapter(), Settings, TestContext.Current.CancellationToken));

        Assert.Equal(TemplateErrorCodes.RenderTimeout, refusal.Code);
        Assert.Contains("60 s", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("not produced within", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProductionPathInstallsNoStallAndComposesTheSameBytes()
    {
        var composer = new BlockComposer(new ManualTimeProvider(Now));

        Assert.Null(composer.Stall);

        var block = await composer.ComposeAsync(Request(), new ScribanTemplateAdapter(), Settings, TestContext.Current.CancellationToken);

        Assert.Equal("<html><body><tr><td>1</td></tr></body></html>", block.Text);
    }

    [Fact]
    public async Task AStallBelowTheDeadlineLeavesTheComposedBlockUnchanged()
    {
        var clock = new ManualTimeProvider(Now);
        var stalled = new BlockComposer(clock)
        {
            Stall = new AdvancingStall(clock, TimeSpan.FromSeconds(30)),
        };
        var production = new BlockComposer(new ManualTimeProvider(Now));

        var afterStall = await stalled.ComposeAsync(Request(), new ScribanTemplateAdapter(), Settings, TestContext.Current.CancellationToken);
        var baseline = await production.ComposeAsync(Request(), new ScribanTemplateAdapter(), Settings, TestContext.Current.CancellationToken);

        Assert.Equal(baseline.Html, afterStall.Html);
        Assert.Equal(
            baseline.Fragments.Select(fragment => (fragment.Kind, fragment.Start, fragment.Length)),
            afterStall.Fragments.Select(fragment => (fragment.Kind, fragment.Start, fragment.Length)));
    }

    [Fact]
    public async Task TheStallIsAwaitedWithTheStepsOwnTokenAtTheCompositionPhase()
    {
        var stall = new RecordingStall();
        var composer = new BlockComposer(new ManualTimeProvider(Now)) { Stall = stall };
        using var cancellation = new CancellationTokenSource();

        await composer.ComposeAsync(Request(), new ScribanTemplateAdapter(), Settings, cancellation.Token);

        Assert.Equal(RenderPhase.Composition, stall.Phase);
        Assert.Equal(cancellation.Token, stall.Token);
        Assert.True(stall.Token.CanBeCanceled);
    }

    private static CompositionRequest Request()
    {
        var parts = new (TemplatePart Part, string Source)[]
        {
            (TemplatePart.DocumentStart, "<html><body>"),
            (TemplatePart.Row, "<tr><td>{{ item.sequence }}</td></tr>"),
            (TemplatePart.DocumentEnd, "</body></html>"),
        };
        var manifest = new RenderManifest(
            ScribanTemplateAdapter.EngineName,
            ScribanTemplateAdapter.EngineVersion,
            parts.ToDictionary(part => part.Part, part => part.Part.ToString(), EqualityComparer<TemplatePart>.Default),
            [],
            new PdfSettings("A4", "portrait", new PageMarginsMm(15, 12, 18, 12), PageTotalsEnabled: false, VerificationAnchors.None),
            new LocaleDefaults("en", "UTC"),
            GroupKey: null);
        var files = new MemoryPackageFiles(parts.ToDictionary(part => part.Part.ToString(), part => part.Source, StringComparer.Ordinal));
        var template = new ScribanTemplateAdapter().Prepare(manifest, files);

        return new CompositionRequest(
            template,
            new CompositionBlock(0, 1, 1, IsFirst: true, IsLast: true),
            RenderContextFactory.Header("{}"u8.ToArray()),
            [new ComposedRecord(1, RenderContextFactory.Header(Encoding.UTF8.GetBytes("""{"sequence":1}""")))],
            new CompositionRender("en", "UTC", DateTimeOffset.UnixEpoch),
            GroupKey: null);
    }

    /// <summary>A stall that moves the block's clock and returns, so the guard decides the rest.</summary>
    private sealed class AdvancingStall(ManualTimeProvider clock, TimeSpan by) : IRenderStall
    {
        public Task StallAsync(RenderPhase phase, CancellationToken cancellationToken)
        {
            Assert.Equal(RenderPhase.Composition, phase);
            clock.Advance(by);
            return Task.CompletedTask;
        }
    }

    /// <summary>A stall that records the phase and the token the composer handed it.</summary>
    private sealed class RecordingStall : IRenderStall
    {
        public RenderPhase Phase { get; private set; }

        public CancellationToken Token { get; private set; }

        public Task StallAsync(RenderPhase phase, CancellationToken cancellationToken)
        {
            Phase = phase;
            Token = cancellationToken;
            return Task.CompletedTask;
        }
    }
}
