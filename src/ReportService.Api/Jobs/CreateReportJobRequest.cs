namespace ReportService.Api.Jobs;

/// <summary>
/// What a consumer asks for when it creates a report job: the template it wants,
/// the upload whose data is to be reported on, the profile that data declares, the
/// outputs it wants and, when it names it, the presentation the report is written
/// in. Every member is the raw string the request carried; whether those strings
/// name something this increment offers is decided after the document is read, so a
/// well-formed request for an unsupported thing stays distinguishable from a
/// malformed one.
/// </summary>
/// <param name="TemplateId">The template's identifier, as received.</param>
/// <param name="TemplateVersion">The version the request named, as received; null when it named none.</param>
/// <param name="UploadId">The identifier of the upload the job reports on, as received.</param>
/// <param name="Profile">The declared data profile, as received.</param>
/// <param name="Outputs">The outputs the request asked for, in the order it named them.</param>
/// <param name="Culture">The culture the request named, as received; null when it named none.</param>
/// <param name="TimeZone">The time zone the request named, as received; null when it named none.</param>
public sealed record CreateReportJobRequest(
    string TemplateId,
    string? TemplateVersion,
    string UploadId,
    string Profile,
    IReadOnlyList<string> Outputs,
    string? Culture,
    string? TimeZone);
