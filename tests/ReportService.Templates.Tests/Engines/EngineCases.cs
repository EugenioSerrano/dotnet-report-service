using System.Globalization;
using System.Text;
using ReportService.Testing;

namespace ReportService.Templates.Tests;

/// <summary>
/// The fixture of a per-engine case: one package of a few parts, one header, a few records
/// and the block's presentation settings, composed through the engine the case names.
/// </summary>
internal static class EngineCases
{
    public static readonly DateTimeOffset Clock = new(2026, 11, 2, 2, 30, 0, TimeSpan.Zero);

    public static PresentationSettings BuenosAires { get; } =
        new(CultureInfo.GetCultureInfo("es-AR"), TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"), Clock);

    public static PresentationSettings Tokyo { get; } =
        new(CultureInfo.GetCultureInfo("en-US"), TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"), Clock);

    public static RenderManifest Manifest(string engine, params (TemplatePart Part, string Source)[] parts) =>
        new(
            engine,
            EngineVersionOf(engine),
            parts.ToDictionary(part => part.Part, part => PartPath(engine, part.Part), EqualityComparer<TemplatePart>.Default),
            [],
            new PdfSettings("A4", "portrait", new PageMarginsMm(15, 12, 18, 12), PageTotalsEnabled: false, VerificationAnchors.None),
            new LocaleDefaults("es-AR", "America/Argentina/Buenos_Aires"),
            GroupKey: null);

    public static string PartPath(string engine, TemplatePart part) =>
        "html/" + TemplatePartNames.Of(part) + ".html." + ExtensionOf(engine);

    public static string ExtensionOf(string engine) => engine switch
    {
        HandlebarsTemplateAdapter.EngineName => "hbs",
        FluidTemplateAdapter.EngineName => "liquid",
        _ => "sbn",
    };

    public static string EngineVersionOf(string engine) => engine switch
    {
        HandlebarsTemplateAdapter.EngineName => HandlebarsTemplateAdapter.EngineVersion,
        FluidTemplateAdapter.EngineName => FluidTemplateAdapter.EngineVersion,
        _ => ScribanTemplateAdapter.EngineVersion,
    };

    public static ITemplateEngineAdapter AdapterOf(string engine) => engine switch
    {
        HandlebarsTemplateAdapter.EngineName => new HandlebarsTemplateAdapter(),
        FluidTemplateAdapter.EngineName => new FluidTemplateAdapter(),
        _ => new ScribanTemplateAdapter(),
    };

    public static ComposedBlock Compose(
        string engine,
        (TemplatePart Part, string Source)[] parts,
        string header,
        string[] records,
        PresentationSettings? settings = null,
        string? groupKey = null)
    {
        var adapter = AdapterOf(engine);
        var prepared = adapter.Prepare(Manifest(engine, parts), Files(engine, parts));
        var composer = new BlockComposer(new ManualTimeProvider(Clock));
        return composer.Compose(
            new CompositionRequest(
                prepared,
                new CompositionBlock(0, records.Length, 1, IsFirst: true, IsLast: true),
                RenderContextFactory.Header(Encoding.UTF8.GetBytes(header)),
                [.. records.Select((json, index) => new ComposedRecord(
                    index + 1,
                    RenderContextFactory.Header(Encoding.UTF8.GetBytes(json))))],
                new CompositionRender(
                    (settings ?? BuenosAires).Culture.Name,
                    (settings ?? BuenosAires).TimeZone.Id,
                    (settings ?? BuenosAires).Clock),
                groupKey),
            adapter,
            settings ?? BuenosAires);
    }

    public static MemoryPackageFiles Files(string engine, (TemplatePart Part, string Source)[] parts) =>
        new(parts.ToDictionary(part => PartPath(engine, part.Part), part => part.Source, StringComparer.Ordinal));

    public static PreparedTemplate Prepare(
        string engine,
        (TemplatePart Part, string Source)[] parts,
        ITemplatePackageFiles? files = null) =>
        AdapterOf(engine).Prepare(Manifest(engine, parts), files ?? Files(engine, parts));
}
