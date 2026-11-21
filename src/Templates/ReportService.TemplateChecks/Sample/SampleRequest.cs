using ReportService.TemplatePackages.Manifest;
using ReportService.Templates;

namespace ReportService.TemplateChecks.Sample;

/// <summary>
/// What one sample render is asked for: the version being taken, the package the
/// take copied to its scratch directory, and the bytes of the version's own
/// <c>sample.json</c>. The publication descriptor's hash travels beside them as
/// provenance - a version is read from the object store, so there is no commit to
/// name - and is never read by the render itself.
/// <para>
/// <see cref="EmptySample"/> asks for the same sample with zero records: the
/// zero-record document a report must still print. It is one request with one flag
/// rather than a second sample, because the empty sample is the same version
/// rendering the same header with no record.
/// </para>
/// </summary>
/// <param name="TemplateId">The template's identifier.</param>
/// <param name="Version">The version being taken.</param>
/// <param name="Manifest">The typed view of the version's manifest.</param>
/// <param name="Files">The files of the take's scratch copy.</param>
/// <param name="SampleJson">The bytes of the package's <c>sample.json</c>.</param>
/// <param name="SourceDescriptorSha256">The publication record's hash the take verified, when one is known.</param>
/// <param name="EmptySample">Whether the same sample is rendered with zero records.</param>
/// <param name="OutputDirectory">
/// A directory the verified sample PDF is copied into, or <see langword="null"/> when
/// nothing is kept. The take never names one - a sample artifact is never kept - and only
/// the rendering host's demo does, so the demo prints through the same path.
/// </param>
public sealed record SampleRequest(
    string TemplateId,
    string Version,
    TemplateManifest Manifest,
    ITemplatePackageFiles Files,
    ReadOnlyMemory<byte> SampleJson,
    string? SourceDescriptorSha256 = null,
    bool EmptySample = false,
    string? OutputDirectory = null);
