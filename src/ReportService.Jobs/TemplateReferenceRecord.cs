using ReportService.TemplatePackages.Records;

namespace ReportService.Jobs;

/// <summary>
/// The template a job asked for, as it was written in the request, and what the
/// job's template step has written about it since: the release version a by-name
/// reference may fall back to when the source cannot be checked, recorded at
/// acceptance, and the pin the step commits once. The reference itself - the
/// template, the form and the version the consumer named - never changes; the
/// baseline is written with the job and the pin is written once, so a reader of
/// the record can always say which version a job uses or that it has none yet.
/// </summary>
/// <param name="ReportJobId">The job the reference belongs to.</param>
/// <param name="TemplateId">The template's identifier, in its key grammar.</param>
/// <param name="ReferenceForm">Whether a version was named or only a name.</param>
public sealed record TemplateReferenceRecord(
    string ReportJobId,
    string TemplateId,
    TemplateReferenceForm ReferenceForm)
{
    /// <summary>The version the consumer named; null for a reference by name.</summary>
    public string? RequestedVersion { get; init; }

    /// <summary>
    /// The release version recorded at acceptance for a by-name reference; null for an
    /// exact reference and for a template with no usable release taken at that instant.
    /// </summary>
    public string? BaselineVersion { get; init; }

    /// <summary>The version the job is fixed to, once its template step has pinned it; null before.</summary>
    public TemplatePin? Pin { get; init; }
}
