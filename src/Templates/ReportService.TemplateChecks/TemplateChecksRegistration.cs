using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReportService.TemplateChecks.Checks;
using ReportService.TemplateChecks.PdfConstraints;
using ReportService.TemplateChecks.Sample;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks;

/// <summary>
/// The URL the render serves a composed document from, which the asset rule
/// resolves a part's relative references against. It is a constant of this
/// project: the checks do not reference the printing assembly (which carries the
/// browser package) for one string, and a hermetic test asserts this value equals
/// the route policy's own.
/// </summary>
public static class TemplateDocumentBase
{
    /// <summary>The document's URL.</summary>
    public const string Url = "https://report.invalid/document.html";
}

/// <summary>
/// Registers the take checks in the fixed order. The order is the contract: the same
/// package always reports the same first refusal, and a later check may trust what an
/// earlier one confirmed. The fifteen checks are the thirteen static ones, the sample
/// render and the record at the row bound last: the sample render needs the process to
/// have registered the <c>ISampleRenderer</c> of the renderer it runs on, and the
/// at-bound record is the most expensive render and the last thing between a well-formed
/// package and its being kept. The process also supplies the <c>NumberingCapacity</c> the
/// numbering check measures at - its configured document capacity; nothing here supplies one.
/// </summary>
public static class TemplateChecksRegistration
{
    /// <summary>The fixed order, as the checks' own names.</summary>
    public static IReadOnlyList<string> Order { get; } =
    [
        "ManifestAndPathsCheck",
        "SchemaCheck",
        "SampleCheck",
        "AnchorCheck",
        "SyntaxCheck",
        "IncludeAndAssetCheck",
        "TemplateLintCheck",
        "EngineVersionCheck",
        "NumberingContractCheck",
        "AccountedDetailMarkupCheck",
        "AccountingFooterCheck",
        "NamedFontFamilyCheck",
        "BottomAlignedGridCellCheck",
        "PageCodeDeclarationCheck",
        "sample-render",
        "at-bound-sample",
    ];

    /// <summary>Registers the checks the take runs, in the fixed order.</summary>
    /// <param name="services">The process's service collection.</param>
    public static IServiceCollection AddTemplateVersionChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<TemplateCheckSession>();
        services.AddSingleton<ITemplateVersionCheck, ManifestAndPathsCheck>();
        services.AddSingleton<ITemplateVersionCheck, SchemaCheck>();
        services.AddSingleton<ITemplateVersionCheck, SampleCheck>();
        services.AddSingleton<ITemplateVersionCheck, AnchorCheck>();
        services.AddSingleton<ITemplateVersionCheck, SyntaxCheck>();
        services.AddSingleton<ITemplateVersionCheck, IncludeAndAssetCheck>();
        services.AddSingleton<ITemplateVersionCheck, TemplateLintCheck>();
        services.AddSingleton<ITemplateVersionCheck, EngineVersionCheck>();
        services.AddSingleton<ITemplateVersionCheck, NumberingContractCheck>();
        services.AddSingleton<ITemplateVersionCheck, AccountedDetailMarkupCheck>();
        services.AddSingleton<ITemplateVersionCheck, AccountingFooterCheck>();
        services.AddSingleton<ITemplateVersionCheck, NamedFontFamilyCheck>();
        services.AddSingleton<ITemplateVersionCheck, BottomAlignedGridCellCheck>();
        services.AddSingleton<ITemplateVersionCheck, PageCodeDeclarationCheck>();
        services.AddSingleton<ITemplateVersionCheck, SampleRenderCheck>();
        services.AddSingleton<ITemplateVersionCheck, AtBoundSampleCheck>();
        return services;
    }
}
