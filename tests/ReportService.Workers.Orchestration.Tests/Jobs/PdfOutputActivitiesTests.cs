using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The two metadata steps of a job's publication, as plain classes: the lookup of the one PDF
/// output the workflow starts, and the publication of the verified block as the output's one
/// artifact. The store is the in-memory double the whole suite runs on, so what the steps write
/// is read back the way the workflow and the job's steps read it.
/// </summary>
public sealed class PdfOutputActivitiesTests
{
    private const string Consumer = "dev-consumer";

    private static readonly DateTimeOffset Now = new(2026, 11, 5, 2, 30, 0, TimeSpan.Zero);

    private static readonly string Hash = new('a', 64);

    private int _sequence;

    [Fact]
    public async Task TheOnePdfOutputOfAJobIsTheOneTheWorkflowStarts()
    {
        var (jobs, uploads) = Stores();
        var acceptance = await AcceptedAsync(jobs, uploads);
        var activity = Activity(jobs);

        var found = await new ActivityEnvironment().RunAsync(() => activity.FindPdfOutputAsync(
            new FindPdfOutputInput(acceptance.ReportJobId)));

        Assert.Equal(acceptance.OutputId, found.OutputId);
    }

    [Fact]
    public async Task AJobThatIsNotRecordedEndsTheStepWithoutRetrying()
    {
        var (jobs, _) = Stores();
        var activity = Activity(jobs);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.FindPdfOutputAsync(
                new FindPdfOutputInput("job-0123456789abcdef0123456789abcdef"))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task PublishingWritesTheOneArtifactOfTheOutputAndMarksItSucceeded()
    {
        var (jobs, uploads) = Stores();
        var acceptance = await AcceptedAsync(jobs, uploads);
        Assert.Equal(
            TransitionOutcome.Applied,
            await jobs.TransitionOutputAsync(acceptance.OutputId, Now, TestContext.Current.CancellationToken));
        var activity = Activity(jobs);

        var published = await new ActivityEnvironment().RunAsync(() => activity.PublishOutputAsync(
            new PublishOutputInput(
                acceptance.ReportJobId,
                acceptance.OutputId,
                $"outputs/{acceptance.ReportJobId}/{acceptance.OutputId}/attempt",
                Hash,
                2_048,
                3)));

        Assert.Equal(PublishOutputResult.Published, published.Outcome);
        Assert.Matches(new Regex("^artifact-[0-9a-f]{32}$", RegexOptions.CultureInvariant), published.ArtifactId);

        var state = await jobs.FindForWorkflowAsync(acceptance.ReportJobId, TestContext.Current.CancellationToken);
        var output = Assert.Single(state!.Outputs);
        Assert.Equal(OutputStatus.Succeeded, output.Status);
        var view = await jobs.FindStatusAsync(acceptance.ReportJobId, Consumer, TestContext.Current.CancellationToken);
        Assert.Equal(published.ArtifactId, Assert.Single(view!.Outputs).ArtifactId);
    }

    [Fact]
    public async Task PublishingTheSameOutputAgainAnswersTheArtifactThatExistsAndWritesNothing()
    {
        var (jobs, uploads) = Stores();
        var acceptance = await AcceptedAsync(jobs, uploads);
        await jobs.TransitionOutputAsync(acceptance.OutputId, Now, TestContext.Current.CancellationToken);
        var activity = Activity(jobs);
        var input = new PublishOutputInput(
            acceptance.ReportJobId,
            acceptance.OutputId,
            $"outputs/{acceptance.ReportJobId}/{acceptance.OutputId}/attempt",
            Hash,
            2_048,
            3);

        var first = await new ActivityEnvironment().RunAsync(() => activity.PublishOutputAsync(input));
        var second = await new ActivityEnvironment().RunAsync(() => activity.PublishOutputAsync(input));

        Assert.Equal(PublishOutputResult.Published, first.Outcome);
        Assert.Equal(PublishOutputResult.AlreadyPublished, second.Outcome);
        Assert.Equal(first.ArtifactId, second.ArtifactId);
    }

    [Fact]
    public async Task PublishingAnOutputThatIsNotRunningIsRefusedWithoutWritingAnything()
    {
        var (jobs, uploads) = Stores();
        var acceptance = await AcceptedAsync(jobs, uploads);
        var activity = Activity(jobs);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.PublishOutputAsync(
                new PublishOutputInput(
                    acceptance.ReportJobId,
                    acceptance.OutputId,
                    $"outputs/{acceptance.ReportJobId}/{acceptance.OutputId}/attempt",
                    Hash,
                    2_048,
                    3))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        var view = await jobs.FindStatusAsync(acceptance.ReportJobId, Consumer, TestContext.Current.CancellationToken);
        var output = Assert.Single(view!.Outputs);
        Assert.Equal(OutputStatus.Pending, output.Status);
        Assert.Null(output.ArtifactId);
    }

    [Fact]
    public void AnArtifactIdentifierIsThePrefixAndOneHundredAndTwentyEightRandomBits()
    {
        var first = PdfOutputActivities.NewArtifactId();
        var second = PdfOutputActivities.NewArtifactId();

        Assert.StartsWith(PdfOutputActivities.ArtifactPrefix, first, StringComparison.Ordinal);
        Assert.Equal(PdfOutputActivities.ArtifactPrefix.Length + 32, first.Length);
        Assert.All(first[PdfOutputActivities.ArtifactPrefix.Length..], character => Assert.True(char.IsAsciiHexDigitLower(character)));
        Assert.NotEqual(first, second);
    }

    private static (InMemoryJobStore Jobs, InMemoryUploadStore Uploads) Stores()
    {
        var uploads = new InMemoryUploadStore();
        return (new InMemoryJobStore(uploads), uploads);
    }

    private static PdfOutputActivities Activity(IJobStore jobs) => new(
        jobs,
        new FixedTimeProvider(Now),
        NullLoggerFactory.Instance,
        NullLogger<PdfOutputActivities>.Instance);

    // A job accepted over an upload the service could have accepted it on: every write of the
    // upload goes through the upload store's own steps, so the job starts from a reachable state.
    private async Task<JobAcceptance> AcceptedAsync(InMemoryJobStore jobs, InMemoryUploadStore uploads)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = UploadIdentifier.Prefix + (++_sequence).ToString("x32", CultureInfo.InvariantCulture);
        var stagingKey = ObjectKey.FromSegments("staging", uploadId, "content");
        await uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                Consumer,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                stagingKey,
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

        var snapshotKey = ObjectKey.FromSegments("snapshots", uploadId, "a");
        await uploads.CommitReadyAsync(
            new SnapshotRecord(
                uploadId,
                Consumer,
                snapshotKey,
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
