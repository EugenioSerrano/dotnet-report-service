using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// One job accepted over a ready upload, in the in-memory stores the component cases run on:
/// the upload is driven through its own steps so the acceptance starts from a state the
/// service can reach, and every instant is fixed so a case decides exactly.
/// </summary>
internal static class ReportJobFixture
{
    /// <summary>The consumer every case accepts its jobs for.</summary>
    public const string Consumer = "dev-consumer";

    /// <summary>The instant every case decides at.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 20, 20, 0, 0, TimeSpan.Zero);

    /// <summary>The lower-case hexadecimal hash every case of this fixture uses.</summary>
    public static readonly string Hash = new('a', 64);

    private static int _sequence;

    /// <summary>The two stores a case runs on, both in memory.</summary>
    /// <returns>The job records and the uploads an acceptance is judged against.</returns>
    public static (InMemoryJobStore Jobs, InMemoryUploadStore Uploads) Stores()
    {
        var uploads = new InMemoryUploadStore();
        return (new InMemoryJobStore(uploads), uploads);
    }

    /// <summary>Accepts one job over a ready upload and answers the acceptance.</summary>
    /// <param name="stores">The two stores of the case.</param>
    /// <returns>The job's acceptance.</returns>
    public static async Task<JobAcceptance> AcceptedAsync((InMemoryJobStore Jobs, InMemoryUploadStore Uploads) stores)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobs, uploads) = stores;
        var uploadId = UploadIdentifier.Prefix + (++_sequence).ToString("x32", System.Globalization.CultureInfo.InvariantCulture);

        await uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                Consumer,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                ObjectKey.FromSegments("staging", uploadId, "content"),
                Now.AddHours(-1),
                Now.AddHours(1)),
            cancellationToken);
        Assert.True(await uploads.TryStartTransferAsync(
            uploadId, Consumer, UploadContentEncoding.None, Now.AddMinutes(-30), cancellationToken));
        await uploads.RecordTransferAsync(
            uploadId,
            UploadContentOutcome.Complete,
            2_048,
            2_048,
            Hash,
            Now.AddMinutes(-30),
            cancellationToken);
        await uploads.ConfirmAsync(
            uploadId, Consumer, null, Now.AddMinutes(-30), Now.AddMinutes(-30), Now.AddHours(5), cancellationToken);

        await uploads.CommitReadyAsync(
            new SnapshotRecord(
                uploadId,
                Consumer,
                ObjectKey.FromSegments("snapshots", uploadId, "a"),
                UploadContentEncoding.None,
                DataProfile.HeaderItemsV1,
                Hash,
                2_048,
                Hash,
                2_048,
                Now.AddMinutes(-20)),
            Now.AddMinutes(-20),
            cancellationToken);

        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            [new RequestedOutput(JobIdentifier.CreateOutput(), OutputFormat.Pdf)],
            Consumer,
            uploadId,
            "pilot-grouped",
            TemplateReferenceForm.Exact,
            "1.0.0",
            AcceptanceIdentity.For(uploadId));
        Assert.IsType<JobAcceptanceResult.Accepted>(await jobs.AcceptAsync(acceptance, Now, cancellationToken));
        return acceptance;
    }
}
