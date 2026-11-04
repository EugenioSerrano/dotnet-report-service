using System.Globalization;
using System.Net;
using System.Text.Json;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// Reading one report job through the API's real pipeline: the answer's fixed member set
/// and fixed order, the state and the step, the counters that only ever rise, the totals
/// that are null until they are known and never a percentage, each output with its own
/// state and the identifier of its file, and the stable code of a job that failed — which
/// is shown inside a successful read. The refusals are the same for a job of another
/// consumer, an absent identifier and a value that cannot be an identifier at all, and a
/// missing identity is refused before any record is touched.
/// </summary>
public sealed class ReportJobStatusEndpointTests : IDisposable
{
    private const string AnotherConsumer = "another-consumer";

    private const string ArtifactId = "artifact-0123456789abcdef0123456789abcdef";

    private const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";

    private static readonly string[] TopMembers =
        ["reportJobId", "status", "stage", "progress", "outputs", "errorCode", "createdAt", "startedAt", "finishedAt"];

    private static readonly string[] ProgressMembers =
        ["recordsRead", "recordsTotal", "blocksCompleted", "blocksTotal", "pagesGenerated"];

    private static readonly string[] OutputMembers = ["format", "status", "artifactId", "errorCode"];

    private static readonly DateTimeOffset Now = JobWireValues.TruncateToMillisecond(JobApiHost.Now);

    private readonly JobApiHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task AValidatingJobAnswersItsStageAndItsCounters()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobId, outputId) = await AcceptedAsync();
        await _host.Jobs.TransitionJobAsync(jobId, [ReportJobStatus.Queued], ReportJobStatus.Validating, Now, cancellationToken);
        await _host.Jobs.TransitionOutputAsync(outputId, Now, cancellationToken);
        await _host.Jobs.RecordRecordsReadAsync(jobId, 300, cancellationToken);
        await _host.Jobs.CommitPartitionAsync(Partition(jobId, 1_000), cancellationToken);

        using var response = await GetAsync(JobApiHost.Consumer, jobId);
        var answer = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        AssertExactly(answer, TopMembers);
        Assert.Equal(jobId, answer.GetProperty("reportJobId").GetString());
        Assert.Equal("validating", answer.GetProperty("status").GetString());
        Assert.Equal("validating-data", answer.GetProperty("stage").GetString());

        var progress = answer.GetProperty("progress");
        AssertExactly(progress, ProgressMembers);
        Assert.Equal(1_000, progress.GetProperty("recordsRead").GetInt64());
        Assert.Equal(1_000, progress.GetProperty("recordsTotal").GetInt64());
        Assert.Equal(0, progress.GetProperty("blocksCompleted").GetInt32());
        Assert.Equal(1, progress.GetProperty("blocksTotal").GetInt32());
        Assert.Equal(0, progress.GetProperty("pagesGenerated").GetInt64());

        var output = Assert.Single(answer.GetProperty("outputs").EnumerateArray());
        AssertExactly(output, OutputMembers);
        Assert.Equal("pdf", output.GetProperty("format").GetString());
        Assert.Equal("running", output.GetProperty("status").GetString());
        Assert.Null(output.GetProperty("artifactId").GetString());
        Assert.Null(output.GetProperty("errorCode").GetString());
        Assert.Null(answer.GetProperty("errorCode").GetString());
        AssertNoPercentage(answer);
    }

    [Fact]
    public async Task ARunningJobAnswersTheStepItIsIn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobId, _) = await AcceptedAsync();
        await _host.Jobs.TransitionJobAsync(jobId, [ReportJobStatus.Queued], ReportJobStatus.Validating, Now, cancellationToken);
        await _host.Jobs.TransitionJobAsync(jobId, [ReportJobStatus.Validating], ReportJobStatus.Running, Now, cancellationToken);

        using var response = await GetAsync(JobApiHost.Consumer, jobId);
        var answer = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("running", answer.GetProperty("status").GetString());
        Assert.Equal("rendering-pdf", answer.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task BeforeItsPartitionTheTotalsArePresentAndNullAndNoAnswerCarriesAPercentage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Every state the product can reach: queued, validating, running, failed and — on
        // another job — succeeded. Each answer carries exactly the fixed member set.
        var (queued, _) = await AcceptedAsync();
        var (validating, _) = await AcceptedAsync();
        await _host.Jobs.TransitionJobAsync(validating, [ReportJobStatus.Queued], ReportJobStatus.Validating, Now, cancellationToken);
        var (running, _) = await AcceptedAsync();
        await _host.Jobs.TransitionJobAsync(running, [ReportJobStatus.Queued], ReportJobStatus.Validating, Now, cancellationToken);
        await _host.Jobs.TransitionJobAsync(running, [ReportJobStatus.Validating], ReportJobStatus.Running, Now, cancellationToken);
        var (failed, _) = await AcceptedAsync();
        await _host.Jobs.FailJobAsync(failed, "QUOTA_EXCEEDED", "block", Now, cancellationToken);
        var (succeeded, succeededOutput) = await AcceptedAsync();
        await _host.Jobs.TransitionOutputAsync(succeededOutput, Now, cancellationToken);
        await _host.Jobs.PublishAsync(Artifact(succeeded, succeededOutput), cancellationToken);
        await _host.Jobs.FinalizeJobAsync(succeeded, Now, cancellationToken);

        foreach (var jobId in new[] { queued, validating, running, failed, succeeded })
        {
            using var response = await GetAsync(JobApiHost.Consumer, jobId);
            var answer = await JobApiHost.AnswerAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertExactly(answer, TopMembers);
            AssertExactly(answer.GetProperty("progress"), ProgressMembers);
            AssertExactly(Assert.Single(answer.GetProperty("outputs").EnumerateArray()), OutputMembers);
            AssertNoPercentage(answer);

            if (jobId != succeeded)
            {
                // A total that was never written is null, even for a job that ended before
                // its partition: never zero, which would be a known, empty count.
                Assert.Equal(JsonValueKind.Null, answer.GetProperty("progress").GetProperty("recordsTotal").ValueKind);
                Assert.Equal(JsonValueKind.Null, answer.GetProperty("progress").GetProperty("blocksTotal").ValueKind);
            }
        }
    }

    [Fact]
    public async Task APublishedJobAnswersSucceededWithTheArtifactOfItsOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobId, outputId) = await AcceptedAsync();
        await _host.Jobs.TransitionOutputAsync(outputId, Now, cancellationToken);
        await _host.Jobs.PublishAsync(Artifact(jobId, outputId), cancellationToken);
        await _host.Jobs.FinalizeJobAsync(jobId, Now.AddMinutes(1), cancellationToken);

        using var response = await GetAsync(JobApiHost.Consumer, jobId);
        var answer = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("succeeded", answer.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("stage").ValueKind);
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("errorCode").ValueKind);
        Assert.Equal(Now.AddMinutes(1), Instant(answer.GetProperty("finishedAt").GetString()));
        var output = Assert.Single(answer.GetProperty("outputs").EnumerateArray());
        Assert.Equal("succeeded", output.GetProperty("status").GetString());
        Assert.Equal(ArtifactId, output.GetProperty("artifactId").GetString());
        Assert.Null(output.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task AFailedJobAndItsOutputCarryTheStableCodeInASuccessfulRead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var (paginated, paginatedOutput) = await AcceptedAsync();
        await _host.Jobs.TransitionOutputAsync(paginatedOutput, Now, cancellationToken);
        await _host.Jobs.FailOutputAsync(paginatedOutput, "PAGINATION_FAILED", Now, cancellationToken);
        await _host.Jobs.FinalizeJobAsync(paginated, Now.AddMinutes(1), cancellationToken);

        var (quota, _) = await AcceptedAsync();
        await _host.Jobs.FailJobAsync(quota, "QUOTA_EXCEEDED", "block", Now, cancellationToken);

        var (expired, _) = await AcceptedAsync();
        await _host.Jobs.FailJobAsync(expired, "INPUT_NOT_FOUND", "input-expired", Now, cancellationToken);

        foreach (var (jobId, code) in new[]
                 {
                     (paginated, "PAGINATION_FAILED"),
                     (quota, "QUOTA_EXCEEDED"),
                     (expired, "INPUT_NOT_FOUND"),
                 })
        {
            using var response = await GetAsync(JobApiHost.Consumer, jobId);
            var answer = await JobApiHost.AnswerAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("failed", answer.GetProperty("status").GetString());
            Assert.Equal(code, answer.GetProperty("errorCode").GetString());
            Assert.Equal(JsonValueKind.Null, answer.GetProperty("stage").ValueKind);
            var output = Assert.Single(answer.GetProperty("outputs").EnumerateArray());
            Assert.Equal("failed", output.GetProperty("status").GetString());
            Assert.Equal(code, output.GetProperty("errorCode").GetString());
            Assert.Null(output.GetProperty("artifactId").GetString());
        }
    }

    [Fact]
    public async Task AnEmptySnapshotIsAJobSucceededWithAKnownZeroTotal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobId, outputId) = await AcceptedAsync();
        await _host.Jobs.CommitPartitionAsync(Partition(jobId, 0), cancellationToken);
        await _host.Jobs.TransitionOutputAsync(outputId, Now, cancellationToken);
        await _host.Jobs.CommitPartAsync(
            new PartCommit(outputId, 0, $"block/{outputId}/0/attempt", Hash, 1_024, 1, Hash, Now),
            cancellationToken);
        await _host.Jobs.PublishAsync(Artifact(jobId, outputId) with { PageCount = 1 }, cancellationToken);
        await _host.Jobs.FinalizeJobAsync(jobId, Now, cancellationToken);

        using var response = await GetAsync(JobApiHost.Consumer, jobId);
        var answer = await JobApiHost.AnswerAsync(response);
        var progress = answer.GetProperty("progress");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("succeeded", answer.GetProperty("status").GetString());
        Assert.Equal(0, progress.GetProperty("recordsRead").GetInt64());
        Assert.Equal(JsonValueKind.Number, progress.GetProperty("recordsTotal").ValueKind);
        Assert.Equal(0, progress.GetProperty("recordsTotal").GetInt64());
        Assert.Equal(1, progress.GetProperty("blocksTotal").GetInt32());
        Assert.Equal(1, progress.GetProperty("blocksCompleted").GetInt32());
        Assert.True(progress.GetProperty("pagesGenerated").GetInt64() >= 1);
        Assert.Equal(ArtifactId, Assert.Single(answer.GetProperty("outputs").EnumerateArray()).GetProperty("artifactId").GetString());
    }

    [Fact]
    public async Task TheOperationalTimesAreUtcWithWholeMillisecondsAndStartNullUntilWorkBegins()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobId, _) = await AcceptedAsync();

        using (var queued = await GetAsync(JobApiHost.Consumer, jobId))
        {
            var answer = await JobApiHost.AnswerAsync(queued);
            Assert.Equal(Now, Instant(answer.GetProperty("createdAt").GetString()));
            Assert.Equal(JsonValueKind.Null, answer.GetProperty("startedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, answer.GetProperty("finishedAt").ValueKind);
        }

        var startedAt = Now.AddSeconds(1);
        await _host.Jobs.TransitionJobAsync(jobId, [ReportJobStatus.Queued], ReportJobStatus.Validating, startedAt, cancellationToken);
        using var response = await GetAsync(JobApiHost.Consumer, jobId);
        var after = await JobApiHost.AnswerAsync(response);
        Assert.Equal(startedAt, Instant(after.GetProperty("startedAt").GetString()));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", after.GetProperty("createdAt").GetString());
    }

    [Fact]
    public async Task AForeignAnAbsentAndAMalformedIdentifierAnswerTheSameNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (foreign, _) = await AcceptedAsync(AnotherConsumer);
        var absent = "job-ffffffffffffffffffffffffffffffff";
        var malformed = new[]
        {
            "job-XYZ",
            "upload-0123456789abcdef0123456789abcdef",
            "job-0123456789abcdef0123456789abcdef0",
            "job-0123456789abcdef0123456789abcdeF",
        };

        var problems = new List<string>();
        foreach (var jobId in new[] { foreign, absent })
        {
            using var response = await GetAsync(JobApiHost.Consumer, jobId);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var answer = await JobApiHost.AnswerAsync(response);
            Assert.Equal(ErrorCodes.InputNotFound, answer.GetProperty("code").GetString());
            Assert.Equal(ReportJobStatusEndpoint.NotFoundTitle, answer.GetProperty("title").GetString());
            Assert.Equal(ReportJobStatusEndpoint.NotFoundDetail, answer.GetProperty("detail").GetString());
            problems.Add(await JobApiHost.ProblemAsync(response));
        }

        var callsBefore = _host.Jobs.FindStatusCalls;
        foreach (var jobId in malformed)
        {
            using var response = await GetAsync(JobApiHost.Consumer, jobId);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            problems.Add(await JobApiHost.ProblemAsync(response));
        }

        Assert.Equal(_host.Jobs.FindStatusCalls, callsBefore);
        Assert.Single(problems.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnUnknownIdentityIsRefusedBeforeAnyReadAndAnInactiveConsumerStillReadsItsJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobId, _) = await AcceptedAsync();

        var callsBefore = _host.Jobs.FindStatusCalls;
        using (var missing = await GetAsync(null, jobId))
        {
            Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
            var answer = await JobApiHost.AnswerAsync(missing);
            Assert.Equal(ErrorCodes.ConsumerUnknown, answer.GetProperty("code").GetString());
        }

        Assert.Equal(callsBefore, _host.Jobs.FindStatusCalls);

        // Reading is kept for a consumer that was made inactive: the job it owns answers.
        var (inactiveJob, _) = await AcceptedAsync(JobApiHost.InactiveConsumer);
        using var response = await GetAsync(JobApiHost.InactiveConsumer, inactiveJob);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("queued", (await JobApiHost.AnswerAsync(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnUnavailableStoreAnswersServiceUnavailableWithoutInternalText()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobId, _) = await AcceptedAsync();
        using var host = new JobApiHost(store => new UnavailableStatusStore(store));

        using var response = await host.Client.SendAsync(
            GetRequest(JobApiHost.Consumer, jobId),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ErrorCodes.ServiceUnavailable, (await JobApiHost.AnswerAsync(response)).GetProperty("code").GetString());
        Assert.DoesNotContain("08006", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeoutException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("the server did not answer", body, StringComparison.Ordinal);
    }

    private async Task<(string JobId, string OutputId)> AcceptedAsync(string consumer = JobApiHost.Consumer)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready, consumer);
        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            JobIdentifier.CreateOutput(),
            consumer,
            uploadId,
            "pilot-grouped",
            TemplateReferenceForm.Exact,
            "1.0.0");
        Assert.IsType<JobAcceptanceResult.Accepted>(await _host.Jobs.AcceptAsync(acceptance, JobApiHost.Now, cancellationToken));
        return (acceptance.ReportJobId, acceptance.OutputId);
    }

    private Task<HttpResponseMessage> GetAsync(string? consumerId, string jobId) =>
        _host.Client.SendAsync(GetRequest(consumerId, jobId), TestContext.Current.CancellationToken);

    private static HttpRequestMessage GetRequest(string? consumerId, string jobId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{jobId}", UriKind.Relative));
        if (consumerId is not null)
        {
            message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumerId);
        }

        return message;
    }

    private static DateTimeOffset Instant(string? text)
    {
        Assert.NotNull(text);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", text);
        return DateTimeOffset.ParseExact(
            text!,
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    private static void AssertExactly(JsonElement element, string[] members) =>
        Assert.Equal(members, element.EnumerateObject().Select(member => member.Name).ToArray());

    private static void AssertNoPercentage(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    Assert.DoesNotContain("percent", member.Name, StringComparison.OrdinalIgnoreCase);
                    AssertNoPercentage(member.Value);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertNoPercentage(item);
                }

                break;

            default:
                break;
        }
    }

    private static PartitionRecord Partition(string jobId, long recordCount) => new(
        jobId,
        1,
        1,
        recordCount,
        24,
        $"index/{jobId}/0/attempt",
        Hash,
        120,
        Now);

    private static ArtifactRecord Artifact(string jobId, string outputId) => new(
        ArtifactId,
        jobId,
        outputId,
        OutputFormat.Pdf,
        "application/pdf",
        $"outputs/{jobId}/{outputId}/attempt",
        Hash,
        2_048,
        3,
        Now);

    // Forwards every member to the store it wraps and refuses the status read as a store
    // that cannot be reached; used to prove the read's 503 without any internal text.
    private sealed class UnavailableStatusStore(IJobStore inner) : IJobStore
    {
        public Task<JobAcceptanceResult> AcceptAsync(JobAcceptance acceptance, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.AcceptAsync(acceptance, now, cancellationToken);

        public Task<JobStateRecord?> FindForWorkflowAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.FindForWorkflowAsync(reportJobId, cancellationToken);

        public Task<TemplateReferenceRecord?> FindTemplateReferenceAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.FindTemplateReferenceAsync(reportJobId, cancellationToken);

        public Task<JobFailureOutcome> FailJobAsync(string reportJobId, string code, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FailJobAsync(reportJobId, code, reason, now, cancellationToken);

        public Task<BeginInputWaitOutcome> BeginInputWaitAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.BeginInputWaitAsync(reportJobId, cancellationToken);

        public Task<EndInputWaitOutcome> EndInputWaitAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.EndInputWaitAsync(reportJobId, cancellationToken);

        public Task<IReadOnlyList<string>> FindInputWaitingJobIdsAsync(string uploadId, CancellationToken cancellationToken) =>
            inner.FindInputWaitingJobIdsAsync(uploadId, cancellationToken);

        public Task<TransitionJobResult> TransitionJobAsync(string reportJobId, IReadOnlyList<ReportJobStatus> from, ReportJobStatus to, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.TransitionJobAsync(reportJobId, from, to, now, cancellationToken);

        public Task<TransitionOutcome> TransitionOutputAsync(string outputId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.TransitionOutputAsync(outputId, now, cancellationToken);

        public Task<PartitionCommitResult> CommitPartitionAsync(PartitionRecord partition, CancellationToken cancellationToken) =>
            inner.CommitPartitionAsync(partition, cancellationToken);

        public Task<PartitionRecord?> FindPartitionAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.FindPartitionAsync(reportJobId, cancellationToken);

        public Task<PartCommitResult> CommitPartAsync(PartCommit commit, CancellationToken cancellationToken) =>
            inner.CommitPartAsync(commit, cancellationToken);

        public Task<PartCommit?> FindPartAsync(string outputId, int blockIndex, CancellationToken cancellationToken) =>
            inner.FindPartAsync(outputId, blockIndex, cancellationToken);

        public Task<PublishResult> PublishAsync(ArtifactRecord artifact, CancellationToken cancellationToken) =>
            inner.PublishAsync(artifact, cancellationToken);

        public Task<OutputFailureOutcome> FailOutputAsync(string outputId, string code, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FailOutputAsync(outputId, code, now, cancellationToken);

        public Task<JobFinalizationResult> FinalizeJobAsync(string reportJobId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FinalizeJobAsync(reportJobId, now, cancellationToken);

        public Task<ReportJobStatusView?> FindStatusAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) =>
            throw new JobStoreUnavailableException("08006", new TimeoutException("the server did not answer"));

        public Task<ProgressWriteOutcome> RecordRecordsReadAsync(string reportJobId, long recordsRead, CancellationToken cancellationToken) =>
            throw new JobStoreUnavailableException("08006", new TimeoutException("the server did not answer"));
    }
}
