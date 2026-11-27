using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using ReportService.Workers.Orchestration.Retention;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The format-parameterised publication of an HTML output (US-008.BOLT-002, SPEC-261125-1224
/// §8.1, Phase C): the one publication transaction (guard + insert-once + mark succeeded) that
/// already models the PDF output publishes an HTML artifact — media type
/// <c>text/html; charset=utf-8</c>, no page count (<c>PageCount = null</c>). The oracles come from
/// the approved intent (ADR-047 §3; US-008 AC-6), never from the implementation (G06). The PDF
/// publication contract is left unchanged (asserted by the unmodified <c>PdfOutputActivitiesTests</c>).
///
/// DESIGNED PUBLIC API (tester, for the executor — SPEC Phase C, an L2 seam: the executor may
/// instead widen PdfOutputActivities.PublishOutput or introduce OutputPublicationActivities; keep
/// the PDF public names stable):
///   const string PdfOutputActivities.HtmlMediaType = "text/html; charset=utf-8";
///   sealed record PublishHtmlOutputInput(string ReportJobId, string OutputId, string ObjectKey, string Sha256, long SizeBytes);
///   [Activity("PublishOutput" or a new name)] Task&lt;PublishOutputResult&gt; PublishHtmlOutputAsync(PublishHtmlOutputInput input);
/// </summary>
public sealed class HtmlOutputPublicationTests
{
    private const string Consumer = "dev-consumer";

    private static readonly DateTimeOffset Now = new(2026, 11, 5, 2, 30, 0, TimeSpan.Zero);

    private static readonly string Hash = new('a', 64);

    private int _sequence;

    [Fact]
    public async Task PublishingAnHtmlOutputWritesOneTextHtmlArtifactWithNoPageCountAndMarksItSucceeded()
    {
        var (jobs, uploads) = Stores();
        var acceptance = await AcceptedAsync(jobs, uploads);
        var htmlOutputId = JobIdentifier.CreateOutput();
        jobs.SeedOutput(acceptance.ReportJobId, htmlOutputId, OutputFormat.Html);
        var activity = Activity(jobs);

        var published = await new ActivityEnvironment().RunAsync(() => activity.PublishHtmlOutputAsync(
            new PublishHtmlOutputInput(
                acceptance.ReportJobId,
                htmlOutputId,
                $"outputs/{acceptance.ReportJobId}/{htmlOutputId}/attempt",
                Hash,
                4_096)));

        Assert.Equal(PublishOutputResult.Published, published.Outcome);

        var state = await jobs.FindForWorkflowAsync(acceptance.ReportJobId, TestContext.Current.CancellationToken);
        var output = Assert.Single(state!.Outputs, item => item.OutputId == htmlOutputId);
        Assert.Equal(OutputStatus.Succeeded, output.Status);

        var artifact = jobs.ArtifactOf(htmlOutputId);
        Assert.NotNull(artifact);
        Assert.Equal(OutputFormat.Html, artifact!.Format);
        Assert.Equal("text/html; charset=utf-8", artifact.MediaType);
        Assert.Null(artifact.PageCount);
        Assert.Equal(4_096, artifact.SizeBytes);
    }

    [Fact]
    public async Task PublishingTheSameHtmlOutputAgainAnswersTheArtifactThatExistsAndWritesNothing()
    {
        var (jobs, uploads) = Stores();
        var acceptance = await AcceptedAsync(jobs, uploads);
        var htmlOutputId = JobIdentifier.CreateOutput();
        jobs.SeedOutput(acceptance.ReportJobId, htmlOutputId, OutputFormat.Html);
        var activity = Activity(jobs);
        var input = new PublishHtmlOutputInput(
            acceptance.ReportJobId, htmlOutputId, $"outputs/{acceptance.ReportJobId}/{htmlOutputId}/attempt", Hash, 4_096);

        var first = await new ActivityEnvironment().RunAsync(() => activity.PublishHtmlOutputAsync(input));
        var second = await new ActivityEnvironment().RunAsync(() => activity.PublishHtmlOutputAsync(input));

        Assert.Equal(PublishOutputResult.Published, first.Outcome);
        Assert.Equal(PublishOutputResult.AlreadyPublished, second.Outcome);
        Assert.Equal(first.ArtifactId, second.ArtifactId);
    }

    [Fact]
    public async Task PublishingAnHtmlOutputThatIsNotRunningIsRefusedWithoutWritingAnything()
    {
        var (jobs, uploads) = Stores();
        var acceptance = await AcceptedAsync(jobs, uploads);
        // An output never transitioned to running: the PDF output the acceptance created is Pending.
        var activity = Activity(jobs);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.PublishHtmlOutputAsync(
                new PublishHtmlOutputInput(
                    acceptance.ReportJobId,
                    acceptance.Outputs[0].OutputId,
                    $"outputs/{acceptance.ReportJobId}/{acceptance.Outputs[0].OutputId}/attempt",
                    Hash,
                    4_096))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Null(jobs.ArtifactOf(acceptance.Outputs[0].OutputId));
    }

    private static (InMemoryJobStore Jobs, InMemoryUploadStore Uploads) Stores()
    {
        var uploads = new InMemoryUploadStore();
        return (new InMemoryJobStore(uploads), uploads);
    }

    private static HtmlOutputActivities Activity(IJobStore jobs) => new(
        jobs,
        new FixedTimeProvider(Now),
        Options.Create(new RetentionOptions { Outputs = TimeSpan.FromDays(7) }),
        NullLoggerFactory.Instance);

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
            uploadId, UploadContentOutcome.Complete, 2_048, 2_048, Hash, Now.AddMinutes(-30), cancellationToken);
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
