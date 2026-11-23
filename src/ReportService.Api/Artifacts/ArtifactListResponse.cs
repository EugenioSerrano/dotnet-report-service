using ReportService.Jobs;
using ReportService.Uploads;

namespace ReportService.Api.Artifacts;

/// <summary>
/// What a job's owner reads when it lists the job's published artifacts: the
/// identifier of the job the listing belongs to and, for each file the job has
/// published, everything a consumer needs to use it — its identifier, the form
/// it was produced in, its media type, its size, the hash the received bytes
/// must match and the instant it stops being downloadable. Every member is
/// always present; an artifact that has expired stays in the list with its past
/// instant, and the read of its bytes is what answers that it expired. The
/// object key, the consumer, the output and the page count never leave the
/// service.
/// </summary>
/// <param name="ReportJobId">The identifier of the job the artifacts belong to.</param>
/// <param name="Artifacts">Its published artifacts, in the fixed format order; empty while it has published none.</param>
public sealed record ArtifactListResponse(
    string ReportJobId,
    IReadOnlyList<ArtifactListItemResponse> Artifacts)
{
    /// <summary>Projects the stored artifacts onto the answer.</summary>
    /// <param name="view">The artifacts as the store read them.</param>
    public static ArtifactListResponse From(JobArtifactsView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new ArtifactListResponse(
            view.ReportJobId,
            [.. view.Artifacts.Select(ArtifactListItemResponse.From)]);
    }
}

/// <summary>One published artifact of a job, as its owner lists it.</summary>
/// <param name="ArtifactId">The identifier the download names.</param>
/// <param name="Format">The form the artifact was produced in, in its answer text.</param>
/// <param name="MimeType">The media type stored with it.</param>
/// <param name="SizeBytes">The size of the stored object, the length of a complete download.</param>
/// <param name="Sha256">The SHA-256 of the stored bytes, lower-case hexadecimal, which the received bytes must match.</param>
/// <param name="ExpiresAt">When it stops being downloadable, in UTC with whole milliseconds.</param>
public sealed record ArtifactListItemResponse(
    string ArtifactId,
    string Format,
    string MimeType,
    long SizeBytes,
    string Sha256,
    string ExpiresAt)
{
    /// <summary>Projects one stored artifact onto its answer member.</summary>
    /// <param name="view">The artifact as the store read it.</param>
    public static ArtifactListItemResponse From(ArtifactView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new ArtifactListItemResponse(
            view.ArtifactId,
            JobWireValues.ToWire(view.Format),
            view.MediaType,
            view.SizeBytes,
            view.Sha256,
            UploadWireValues.FormatTimestamp(view.ExpiresAt));
    }
}
