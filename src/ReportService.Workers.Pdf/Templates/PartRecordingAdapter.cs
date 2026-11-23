using ReportService.Templates;

namespace ReportService.Workers.Pdf.Templates;

/// <summary>
/// The delivered adapter a sample composes with, with the part it is rendering kept
/// beside the call: a failure inside <see cref="RenderPart"/> leaves the part that
/// was rendering, so the refusal can name its package-relative file. It changes
/// nothing the adapter does - preparation and rendering are delegated unchanged -
/// and it is constructed for one sample render only.
/// </summary>
internal sealed class PartRecordingAdapter(ITemplateEngineAdapter inner, RenderManifest manifest) : ITemplateEngineAdapter
{
    private readonly ITemplateEngineAdapter _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    private readonly RenderManifest _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));

    /// <summary>The package-relative file of the part that was rendering, or <see langword="null"/> before the first one.</summary>
    public string? LastFile { get; private set; }

    /// <inheritdoc />
    public string Engine => _inner.Engine;

    /// <inheritdoc />
    public string Version => _inner.Version;

    /// <inheritdoc />
    public PreparedTemplate Prepare(RenderManifest forManifest, ITemplatePackageFiles files) =>
        _inner.Prepare(forManifest, files);

    /// <inheritdoc />
    public void RenderPart(
        PreparedTemplate template,
        TemplatePart part,
        RenderContext context,
        PresentationSettings presentation,
        GuardedWriter writer,
        CancellationToken cancellationToken)
    {
        LastFile = _manifest.PathOf(part) ?? LastFile;
        _inner.RenderPart(template, part, context, presentation, writer, cancellationToken);
    }
}
