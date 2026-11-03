namespace ReportService.TemplatePackages.Manifest;

/// <summary>
/// The engine versions this deployment renders with. A taken version records
/// the version that will render it, so the catalog answers one version per
/// engine and never a range. Until the adapters report their own version, this
/// table is the source of that answer.
/// </summary>
public static class TemplateEngineCatalog
{
    /// <summary>The Scriban version this deployment renders with.</summary>
    public const string ScribanVersion = "7.5.1";

    /// <summary>The Handlebars version this deployment renders with.</summary>
    public const string HandlebarsVersion = "2.4.3";

    /// <summary>The Fluid version this deployment renders with.</summary>
    public const string LiquidVersion = "2.40.0";

    /// <summary>The version that will render a package declaring <paramref name="engine"/>.</summary>
    /// <param name="engine">A declared engine.</param>
    public static string VersionOf(TemplateEngine engine) => engine switch
    {
        TemplateEngine.Scriban => ScribanVersion,
        TemplateEngine.Handlebars => HandlebarsVersion,
        TemplateEngine.LiquidFluid => LiquidVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };
}
