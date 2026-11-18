using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.Jobs;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using ReportService.Workers.Orchestration.Retention;
using ReportService.Workers.Orchestration.Uploads;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The expiry a publication records: the instant the artifact was published plus the output
/// retention, to the millisecond — and, when a retried step meets the row the first attempt
/// wrote, the first expiry again, whatever the setting says by then. A setting changed
/// between two attempts never moves an expiry a consumer was already told.
/// </summary>
public sealed class PublishOutputExpiryTests
{
    private const string Consumer = "dev-consumer";

    private const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";

    // An instant with ticks below the millisecond: the arithmetic of the expiry is exact, and
    // nothing in it drifts with the precision of a response.
    private static readonly DateTimeOffset Now =
        new DateTimeOffset(2026, 11, 17, 3, 41, 2, 456, TimeSpan.Zero).AddTicks(7890);

    private int _sequence;

    [Fact]
    public async Task TheExpiryIsThePublicationInstantPlusTheConfiguredRetention()
    {
        var (jobs, uploads) = Stores();
        var acceptance = await AcceptedAsync(jobs, uploads);
        await jobs.TransitionOutputAsync(acceptance.OutputId, Now, TestContext.Current.CancellationToken);

        var published = await new ActivityEnvironment().RunAsync(() => Activity(jobs, TimeSpan.FromDays(7)).PublishOutputAsync(
            Input(acceptance)));

        Assert.Equal(PublishOutputResult.Published, published.Outcome);
        var artifact = jobs.ArtifactOf(acceptance.OutputId);
        Assert.NotNull(artifact);

        // The instant is whole milliseconds, the precision every answer and every decision uses,
        // and the expiry is exactly the configured retention after it.
        var publishedAt = JobWireValues.TruncateToMillisecond(Now);
        Assert.Equal(publishedAt, artifact!.CreatedAt);
        Assert.Equal(publishedAt.AddDays(7), artifact.ExpiresAt);
        Assert.Equal(TimeSpan.FromDays(7), artifact.ExpiresAt - artifact.CreatedAt);
        Assert.Equal(
            JobWireValues.TruncateToMillisecond(Now.AddDays(7)),
            JobWireValues.TruncateToMillisecond(artifact.ExpiresAt));
    }

    [Fact]
    public async Task ARetryThatMeetsTheFirstAttemptsPublicationKeepsTheFirstExpiry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobs, uploads) = Stores();
        var acceptance = await AcceptedAsync(jobs, uploads);
        await jobs.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        var clock = new ManualTimeProvider(Now);

        var first = await new ActivityEnvironment().RunAsync(() => Activity(jobs, TimeSpan.FromDays(7), clock).PublishOutputAsync(
            Input(acceptance)));

        // The setting changes and the retried step arrives an hour later: the row that exists is
        // answered, and its expiry is the one the first attempt promised.
        clock.Advance(TimeSpan.FromHours(1));
        var retried = await new ActivityEnvironment().RunAsync(() => Activity(jobs, TimeSpan.FromDays(30), clock).PublishOutputAsync(
            Input(acceptance)));

        Assert.Equal(PublishOutputResult.Published, first.Outcome);
        Assert.Equal(PublishOutputResult.AlreadyPublished, retried.Outcome);
        Assert.Equal(first.ArtifactId, retried.ArtifactId);

        var artifact = jobs.ArtifactOf(acceptance.OutputId);
        Assert.NotNull(artifact);
        Assert.Equal(JobWireValues.TruncateToMillisecond(Now.AddDays(7)), artifact!.ExpiresAt);

        // What the changed setting would have written on a fresh publication: thirty days from the
        // retried step's own instant. The row keeps the first promise instead.
        Assert.NotEqual(
            JobWireValues.TruncateToMillisecond(Now.AddHours(1).AddDays(30)),
            artifact.ExpiresAt);
    }

    private static (InMemoryJobStore Jobs, InMemoryUploadStore Uploads) Stores()
    {
        var uploads = new InMemoryUploadStore();
        return (new InMemoryJobStore(uploads), uploads);
    }

    private static PdfOutputActivities Activity(IJobStore jobs, TimeSpan retention, TimeProvider? clock = null) => new(
        jobs,
        clock ?? new FixedTimeProvider(Now),
        Options.Create(new RetentionOptions { Outputs = retention }),
        NullLoggerFactory.Instance,
        NullLogger<PdfOutputActivities>.Instance);

    private static PublishOutputInput Input(JobAcceptance acceptance) => new(
        acceptance.ReportJobId,
        acceptance.OutputId,
        $"outputs/{acceptance.ReportJobId}/{acceptance.OutputId}/attempt",
        Hash,
        2_048,
        3);

    // A job accepted over an upload the service could have accepted it on: every write of the
    // upload goes through the upload store's own steps, so the job starts from a reachable state.
    private async Task<JobAcceptance> AcceptedAsync(InMemoryJobStore jobs, InMemoryUploadStore uploads)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
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
            JobIdentifier.CreateOutput(),
            Consumer,
            uploadId,
            "pilot-grouped",
            TemplateReferenceForm.Exact,
            "1.0.0");
        Assert.IsType<JobAcceptanceResult.Accepted>(await jobs.AcceptAsync(acceptance, Now, cancellationToken));
        return acceptance;
    }
}
