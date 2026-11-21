using ReportService.TemplatePackages.Manifest;
using ReportService.Templates;

namespace ReportService.TemplateChecks.Sample;

/// <summary>
/// The one way the take reaches a render: it hands the version's package, its
/// declared engine and its own sample to an implementation and reads what the
/// render decided. The checks project holds the contract only, so it references
/// no browser, no PDF reader and no engine, and the hermetic suite runs against a
/// double; the PDF worker's implementation is the product's one renderer.
/// </summary>
public interface ISampleRenderer
{
    /// <summary>Renders one version's sample, or the same sample with zero records.</summary>
    /// <param name="request">The version, its package and the sample to render.</param>
    /// <param name="cancellationToken">The take's cancellation token.</param>
    /// <returns>
    /// The facts of a render that finished, the deterministic failure of one that
    /// refused the version, or the cause of one that could not run or did not finish.
    /// </returns>
    Task<SampleAttempt> RenderAsync(SampleRequest request, CancellationToken cancellationToken);
}
