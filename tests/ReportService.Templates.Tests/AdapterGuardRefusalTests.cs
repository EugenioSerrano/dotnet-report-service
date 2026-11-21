using System.Globalization;
using System.Text;
using ReportService.Testing;

namespace ReportService.Templates.Tests;

/// <summary>
/// The caller-side guard's refusal, through each engine the product ships: the deadline is
/// reached inside the engine's own write, the engine reports the failure in its own exception
/// type, and what reaches the caller is the guard's refusal itself - the same instance the
/// writer recorded - never the engine's wrapper and never a package failure. One case per
/// engine, so an adapter cannot be added or changed without one.
/// </summary>
public sealed class AdapterGuardRefusalTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 20, 0, 0, TimeSpan.Zero);

    private static readonly PresentationSettings Settings = new(
        CultureInfo.GetCultureInfo("en-US"),
        TimeZoneInfo.Utc,
        DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(ScribanTemplateAdapter.EngineName)]
    [InlineData(HandlebarsTemplateAdapter.EngineName)]
    [InlineData(FluidTemplateAdapter.EngineName)]
    public void ADeadlineReachedInsideTheEnginesWriteIsTheGuardsOwnRefusal(string engine)
    {
        var adapter = EngineCases.AdapterOf(engine);
        var template = EngineCases.Prepare(
            engine,
            [(TemplatePart.DocumentStart, "<p>{{ header.name }}</p>")]);

        var clock = new ManualTimeProvider(Now);
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        var writer = new GuardedWriter(text, clock);

        // The block's clock starts with the writer; the write is what crosses the deadline.
        clock.Advance(TimeSpan.FromSeconds(61));

        var refusal = Assert.Throws<TemplateGuardException>(() => adapter.RenderPart(
            template,
            TemplatePart.DocumentStart,
            Context(),
            Settings,
            writer,
            CancellationToken.None));

        Assert.Equal(TemplateErrorCodes.RenderTimeout, refusal.Code);
        Assert.Same(writer.Refusal, refusal);
        Assert.IsNotType<TemplatePackageException>(refusal);
    }

    private static RenderContext Context() => new(
        RenderContextFactory.Header(Encoding.UTF8.GetBytes("""{"name":"a"}""")),
        null,
        null,
        RenderContextFactory.Block(0, 1, 1, isFirst: true, isLast: true),
        RenderContextFactory.NoAggregates,
        RenderContextFactory.Render("en-US", "UTC", DateTimeOffset.UnixEpoch));
}
