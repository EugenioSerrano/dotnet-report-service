namespace ReportService.Jobs;

/// <summary>
/// What the service is asked to accept: a job for one consumer over one upload,
/// the template reference exactly as the request wrote it, and the identifiers the
/// records will carry. It is built once by the request that accepts the job, so
/// the store is never handed anything that came straight from a caller unchecked.
/// </summary>
/// <param name="ReportJobId">The identifier the job will have.</param>
/// <param name="OutputId">The identifier its PDF output will have.</param>
/// <param name="ConsumerId">The declared consumer that owns the job.</param>
/// <param name="UploadId">The upload whose data the job reports on.</param>
/// <param name="TemplateId">The template's identifier, as the request wrote it.</param>
/// <param name="ReferenceForm">Whether the request named a version.</param>
/// <param name="RequestedVersion">The version the request named; null for a reference by name.</param>
/// <param name="Fingerprint">The identity of the request, computed from what it asked for and never from state.</param>
public sealed record JobAcceptance(
    string ReportJobId,
    string OutputId,
    string ConsumerId,
    string UploadId,
    string TemplateId,
    TemplateReferenceForm ReferenceForm,
    string? RequestedVersion,
    RequestFingerprint Fingerprint)
{
    /// <summary>
    /// The idempotency key the request carried, kept exactly as it was received; null when
    /// the request carried none. A key is bound to the job this acceptance creates, for the
    /// consumer that sent it, and is never reassigned.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// The release version a by-name reference may fall back to when the template source
    /// cannot be checked, chosen at acceptance from the versions already taken; null for an
    /// exact reference and for a by-name one whose template has no usable release taken.
    /// </summary>
    public string? BaselineVersion { get; init; }

    /// <summary>
    /// The culture the report is presented in, as the request named it; null when the request
    /// named none, in which case the template the job takes supplies it.
    /// </summary>
    public string? Culture { get; init; }

    /// <summary>
    /// The time zone the report is presented in, as the request named it; null when the request
    /// named none, in which case the template the job takes supplies it.
    /// </summary>
    public string? TimeZone { get; init; }
}
