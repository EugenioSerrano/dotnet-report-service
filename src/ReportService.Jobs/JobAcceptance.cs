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
public sealed record JobAcceptance(
    string ReportJobId,
    string OutputId,
    string ConsumerId,
    string UploadId,
    string TemplateId,
    TemplateReferenceForm ReferenceForm,
    string? RequestedVersion);
