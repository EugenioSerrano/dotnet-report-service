using ReportService.TemplateChecks;
using ReportService.TemplateChecks.Checks;
using ReportService.TemplateChecks.PdfConstraints;
using ReportService.TemplateChecks.Sample;
using ReportService.TemplateSource.Checks;

namespace ReportService.Testing.TemplateChecks;

/// <summary>
/// The take's check list as the build registers it, for a test that takes a version through
/// the real take in its own process: the thirteen static checks in the fixed order and the
/// two sample gates, the sample render and the record at the row bound. The sample renderer
/// is the caller's, so an end-to-end case takes the package's static admission for real
/// while the sample render is the in-memory double - the product's own renderer is the
/// render worker's and the rendering level's.
/// </summary>
public sealed class PageAccountingTakeChecks : IDisposable
{
    private readonly TemplateCheckSession _session;

    /// <summary>Creates the list over one session and one sample renderer.</summary>
    /// <param name="clock">The clock the checks' budget reads.</param>
    /// <param name="renderer">The sample renderer the two sample gates call.</param>
    public PageAccountingTakeChecks(TimeProvider clock, ISampleRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(renderer);
        _session = new TemplateCheckSession(clock);
        Checks =
        [
            new ManifestAndPathsCheck(_session),
            new SchemaCheck(_session),
            new SampleCheck(_session, clock),
            new AnchorCheck(_session),
            new SyntaxCheck(_session),
            new IncludeAndAssetCheck(_session),
            new TemplateLintCheck(_session),
            new EngineVersionCheck(),
            new NumberingContractCheck(_session, new NumberingCapacity(10_000)),
            new AccountedDetailMarkupCheck(_session),
            new AccountingFooterCheck(),
            new NamedFontFamilyCheck(_session),
            new BottomAlignedGridCellCheck(_session),
            new SampleRenderCheck(renderer),
            new AtBoundSampleCheck(_session, renderer),
        ];
    }

    /// <summary>The checks, in the order the take runs them.</summary>
    public IReadOnlyList<ITemplateVersionCheck> Checks { get; }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
