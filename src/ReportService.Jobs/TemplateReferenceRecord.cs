namespace ReportService.Jobs;

/// <summary>
/// The template a job asked for, as it was written in the request: the template's
/// identifier, the form of the reference and, for an exact reference, the version
/// the consumer named. Nothing else is decided here — taking the version and
/// rendering it are steps of the job, and they write what they resolved.
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
}
