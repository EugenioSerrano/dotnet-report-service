namespace ReportService.Templates;

/// <summary>
/// The engines this deployment offers, in the one list the composition and the rendering
/// host read. A process registers these adapters and the registry built from them; no
/// configuration key can add an engine, so the offered set is the built one.
/// </summary>
public static class TemplateEngineAdapters
{
    /// <summary>Every adapter the product offers, one per engine.</summary>
    public static IReadOnlyList<ITemplateEngineAdapter> All() =>
    [
        new ScribanTemplateAdapter(),
        new HandlebarsTemplateAdapter(),
        new FluidTemplateAdapter(),
    ];

    /// <summary>The registry of the adapters the product offers.</summary>
    public static TemplateEngineAdapterRegistry Registry() => new(All());
}
