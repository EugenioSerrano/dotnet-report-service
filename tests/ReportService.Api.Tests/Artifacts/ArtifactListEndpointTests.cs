using System.Globalization;
using System.Net;
using System.Text.Json;
using ReportService.Api.Artifacts;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Tests.Jobs;
using ReportService.Jobs;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;

namespace ReportService.Api.Tests.Artifacts;

/// <summary>
/// Listing one report job's published artifacts through the API's real pipeline: the
/// fixed member set and member order of the answer, the values equal to the stored
/// record, the empty list a job that has published nothing answers, the expired
/// artifact that stays listed with its past instant, the empty-snapshot job that
/// lists its one file, and the two artifacts ordered by format whatever the order
/// they were published in. The refusals are the same as the job status read's — a
/// foreign, an absent and a malformed identifier answer the same 404, byte for byte —
/// a missing identity is refused before any record is touched, and an unavailable
/// store answers without a word of its internals.
/// </summary>
public sealed class ArtifactListEndpointTests : IDisposable
{
    private const string AnotherConsumer = "another-consumer";

    private const string ArtifactId = "artifact-0123456789abcdef0123456789abcdef";

    private const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";

    private static readonly string[] TopMembers = ["reportJobId", "artifacts"];

    private static readonly string[] ArtifactMembers =
        ["artifactId", "format", "mimeType", "sizeBytes", "sha256", "expiresAt"];

    private static readonly DateTimeOffset Now = JobWireValues.TruncateToMillisecond(JobApiHost.Now);

    private readonly JobApiHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task APublishedJobListsItsOneArtifactWithTheFixedMemberSet()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobId, outputId) = await AcceptedAsync();
        await _host.Jobs.TransitionOutputAsync(outputId, Now, cancellationToken);
        await _host.Jobs.PublishAsync(Artifact(jobId, outputId), cancellationToken);
        await _host.Jobs.FinalizeJobAsync(jobId, Now.AddMinutes(1), cancellationToken);

        using var response = await ListAsync(JobApiHost.Consumer, jobId);
        var answer = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        AssertExactly(answer, TopMembers);
        Assert.Equal(jobId, answer.GetProperty("reportJobId").GetString());

        var artifact = Assert.Single(answer.GetProperty("artifacts").EnumerateArray());
        AssertExactly(artifact, ArtifactMembers);
        Assert.Equal(ArtifactId, artifact.GetProperty("artifactId").GetString());
        Assert.Equal("pdf", artifact.GetProperty("format").GetString());
        Assert.Equal("application/pdf", artifact.GetProperty("mimeType").GetString());
        Assert.Equal(2_048L, artifact.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(Hash, artifact.GetProperty("sha256").GetString());
        Assert.Equal(Now.AddDays(7), Instant(artifact.GetProperty("expiresAt").GetString()));
    }

    [Fact]
    public async Task AQueuedARunningAndAFailedJobListNoArtifacts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var (queued, _) = await AcceptedAsync();
        var (running, _) = await AcceptedAsync();
        await _host.Jobs.TransitionJobAsync(running, [ReportJobStatus.Queued], ReportJobStatus.Validating, Now, cancellationToken);
        await _host.Jobs.TransitionJobAsync(running, [ReportJobStatus.Validating], ReportJobStatus.Running, Now, cancellationToken);
        var (failed, _) = await AcceptedAsync();
        await _host.Jobs.FailJobAsync(failed, "QUOTA_EXCEEDED", "block", Now, cancellationToken);

        foreach (var jobId in new[] { queued, running, failed })
        {
            using var response = await ListAsync(JobApiHost.Consumer, jobId);
            var answer = await JobApiHost.AnswerAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertExactly(answer, TopMembers);
            Assert.Equal(jobId, answer.GetProperty("reportJobId").GetString());
            Assert.Empty(answer.GetProperty("artifacts").EnumerateArray());
        }
    }

    [Fact]
    public async Task AnExpiredArtifactStaysListedWithItsPastExpiry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobId, outputId) = await AcceptedAsync();
        await _host.Jobs.TransitionOutputAsync(outputId, Now.AddDays(-8), cancellationToken);
        await _host.Jobs.PublishAsync(
            Artifact(jobId, outputId) with { CreatedAt = Now.AddDays(-8), ExpiresAt = Now.AddDays(-1) },
            cancellationToken);
        await _host.Jobs.FinalizeJobAsync(jobId, Now.AddDays(-7), cancellationToken);

        using var response = await ListAsync(JobApiHost.Consumer, jobId);
        var answer = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var artifact = Assert.Single(answer.GetProperty("artifacts").EnumerateArray());
        AssertExactly(artifact, ArtifactMembers);
        var expiresAt = Instant(artifact.GetProperty("expiresAt").GetString());
        Assert.Equal(Now.AddDays(-1), expiresAt);
        Assert.True(expiresAt < Now, "an expired artifact is listed with an instant before now");
    }

    [Fact]
    public async Task AnEmptySnapshotJobListsItsOneArtifact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobId, outputId) = await AcceptedAsync();
        await _host.Jobs.CommitPartitionAsync(Partition(jobId, 0), cancellationToken);
        await _host.Jobs.TransitionOutputAsync(outputId, Now, cancellationToken);
        await _host.Jobs.PublishAsync(Artifact(jobId, outputId) with { PageCount = 1 }, cancellationToken);
        await _host.Jobs.FinalizeJobAsync(jobId, Now, cancellationToken);

        using var response = await ListAsync(JobApiHost.Consumer, jobId);
        var answer = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ArtifactId,
            Assert.Single(answer.GetProperty("artifacts").EnumerateArray()).GetProperty("artifactId").GetString());
    }

    [Fact]
    public async Task TwoArtifactsAreOrderedByFormatWhateverTheOrderTheyWerePublishedIn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // The HTML artifact's identifier sorts before the PDF's, so an answer ordered by
        // identifier alone would invert the two: the format order must decide.
        const string HtmlArtifactId = "artifact-00000000000000000000000000000001";
        var (jobId, pdfOutputId) = await AcceptedAsync();
        var htmlOutputId = JobIdentifier.CreateOutput();
        _host.Jobs.SeedOutput(jobId, htmlOutputId, OutputFormat.Html);
        await _host.Jobs.TransitionOutputAsync(pdfOutputId, Now, cancellationToken);
        await _host.Jobs.PublishAsync(
            new ArtifactRecord(
                HtmlArtifactId, jobId, htmlOutputId, OutputFormat.Html, "text/html",
                $"outputs/{jobId}/{htmlOutputId}/attempt", Hash, 512, null, Now, Now.AddDays(7)),
            cancellationToken);
        await _host.Jobs.PublishAsync(Artifact(jobId, pdfOutputId), cancellationToken);

        using var response = await ListAsync(JobApiHost.Consumer, jobId);
        var answer = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var artifacts = answer.GetProperty("artifacts").EnumerateArray().ToList();
        Assert.Equal(2, artifacts.Count);
        Assert.Equal("pdf", artifacts[0].GetProperty("format").GetString());
        Assert.Equal(ArtifactId, artifacts[0].GetProperty("artifactId").GetString());
        Assert.Equal("html", artifacts[1].GetProperty("format").GetString());
        Assert.Equal(HtmlArtifactId, artifacts[1].GetProperty("artifactId").GetString());
        Assert.Equal("text/html", artifacts[1].GetProperty("mimeType").GetString());
        Assert.Equal(512L, artifacts[1].GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task AForeignAnAbsentAndAMalformedIdentifierAnswerTheStatusReadsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (foreign, _) = await AcceptedAsync(AnotherConsumer);
        var absent = "job-ffffffffffffffffffffffffffffffff";

        var malformed = new[]
        {
            "job-XYZ",
            "job-0123456789abcdef0123456789abcde",
            "job-0123456789abcdef0123456789abcdef0",
            "job-0123456789abcdef0123456789abcdeF",
            "upload-0123456789abcdef0123456789abcdef",
        };
        var values = new List<string> { foreign, absent };
        values.AddRange(malformed);

        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var jobId in values)
        {
            using var response = await ListAsync(JobApiHost.Consumer, jobId);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var answer = await JobApiHost.AnswerAsync(response);
            Assert.Equal(ErrorCodes.InputNotFound, answer.GetProperty("code").GetString());
            Assert.Equal(ReportJobStatusEndpoint.NotFoundTitle, answer.GetProperty("title").GetString());
            Assert.Equal(ReportJobStatusEndpoint.NotFoundDetail, answer.GetProperty("detail").GetString());
            problems[jobId] = await JobApiHost.ProblemAsync(response);
        }

        // A value that cannot be an identifier refers to a job that cannot exist: it is
        // answered without a read.
        var callsBefore = _host.Jobs.ListArtifactCalls;
        foreach (var jobId in malformed)
        {
            using var unread = await ListAsync(JobApiHost.Consumer, jobId);
            Assert.Equal(HttpStatusCode.NotFound, unread.StatusCode);
        }

        Assert.Equal(callsBefore, _host.Jobs.ListArtifactCalls);

        // One answer for every case, and the same one the job's status read gives.
        Assert.Single(problems.Values.Distinct(StringComparer.Ordinal));
        foreach (var jobId in values)
        {
            using var status = await _host.Client.SendAsync(StatusRequest(JobApiHost.Consumer, jobId), cancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
            Assert.Equal(await JobApiHost.ProblemAsync(status), problems[jobId]);
        }
    }

    [Fact]
    public async Task AnUnknownIdentityIsRefusedBeforeAnyReadAndAnInactiveConsumerListsItsJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobId, outputId) = await AcceptedAsync();
        await _host.Jobs.TransitionOutputAsync(outputId, Now, cancellationToken);
        await _host.Jobs.PublishAsync(Artifact(jobId, outputId), cancellationToken);

        var callsBefore = _host.Jobs.ListArtifactCalls;
        foreach (var declared in new[] { (string?)null, "no-such-consumer" })
        {
            using var refused = await ListAsync(declared, jobId);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(
                ErrorCodes.ConsumerUnknown,
                (await JobApiHost.AnswerAsync(refused)).GetProperty("code").GetString());
        }

        Assert.Equal(callsBefore, _host.Jobs.ListArtifactCalls);

        // Listing is kept for a consumer that was made inactive: the job it owns answers.
        var (inactiveJob, inactiveOutput) = await AcceptedAsync(JobApiHost.InactiveConsumer);
        await _host.Jobs.TransitionOutputAsync(inactiveOutput, Now, cancellationToken);
        await _host.Jobs.PublishAsync(Artifact(inactiveJob, inactiveOutput), cancellationToken);
        using var response = await ListAsync(JobApiHost.InactiveConsumer, inactiveJob);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single((await JobApiHost.AnswerAsync(response)).GetProperty("artifacts").EnumerateArray());
    }

    [Fact]
    public async Task AnUnavailableStoreAnswersServiceUnavailableWithoutInternalText()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobId, outputId) = await AcceptedAsync();
        await _host.Jobs.TransitionOutputAsync(outputId, Now, cancellationToken);
        await _host.Jobs.PublishAsync(Artifact(jobId, outputId), cancellationToken);
        using var host = new JobApiHost(store => new UnavailableListStore(store));

        using var response = await host.Client.SendAsync(ListRequest(JobApiHost.Consumer, jobId), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            ErrorCodes.ServiceUnavailable,
            (await JobApiHost.AnswerAsync(response)).GetProperty("code").GetString());
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

    private Task<HttpResponseMessage> ListAsync(string? consumerId, string jobId) =>
        _host.Client.SendAsync(ListRequest(consumerId, jobId), TestContext.Current.CancellationToken);

    private static HttpRequestMessage ListRequest(string? consumerId, string jobId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{jobId}{ArtifactListEndpoint.RouteSuffix}", UriKind.Relative));
        Declare(message, consumerId);
        return message;
    }

    private static HttpRequestMessage StatusRequest(string? consumerId, string jobId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{jobId}", UriKind.Relative));
        Declare(message, consumerId);
        return message;
    }

    private static void Declare(HttpRequestMessage message, string? consumerId)
    {
        if (consumerId is not null)
        {
            message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumerId);
        }
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
        Now,
        Now.AddDays(7));

    // Forwards every member to the store it wraps and refuses the listing as a store
    // that cannot be reached; used to prove the read's 503 without any internal text.
    private sealed class UnavailableListStore(IJobStore inner) : IJobStore
    {
        public Task<JobAcceptanceResult> AcceptAsync(JobAcceptance acceptance, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.AcceptAsync(acceptance, now, cancellationToken);

        public Task<JobStateRecord?> FindForWorkflowAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.FindForWorkflowAsync(reportJobId, cancellationToken);

        public Task<TemplateReferenceRecord?> FindTemplateReferenceAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.FindTemplateReferenceAsync(reportJobId, cancellationToken);

        public Task<JobFailureOutcome> FailJobAsync(string reportJobId, string code, string reason, string? message, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FailJobAsync(reportJobId, code, reason, message, now, cancellationToken);

        public Task<JobFailureOutcome> FailJobAsync(string reportJobId, string code, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FailJobAsync(reportJobId, code, reason, now, cancellationToken);

        public Task<TemplatePinState> PinTemplateAsync(string reportJobId, TemplatePin pin, DateTimeOffset now, CancellationToken cancellationToken, TemplateLocaleDefaults? presentation) =>
            inner.PinTemplateAsync(reportJobId, pin, now, cancellationToken, presentation);

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

        public Task<JobArtifactsView?> ListArtifactsAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) =>
            throw new JobStoreUnavailableException("08006", new TimeoutException("the server did not answer"));

        public Task<ArtifactView?> FindArtifactAsync(string reportJobId, string artifactId, string consumerId, CancellationToken cancellationToken) =>
            inner.FindArtifactAsync(reportJobId, artifactId, consumerId, cancellationToken);

        public Task<OutputFailureOutcome> FailOutputAsync(string outputId, string code, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FailOutputAsync(outputId, code, now, cancellationToken);

        public Task<JobFinalizationResult> FinalizeJobAsync(string reportJobId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FinalizeJobAsync(reportJobId, now, cancellationToken);

        public Task<ReportJobStatusView?> FindStatusAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) =>
            inner.FindStatusAsync(reportJobId, consumerId, cancellationToken);

        public Task<ProgressWriteOutcome> RecordRecordsReadAsync(string reportJobId, long recordsRead, CancellationToken cancellationToken) =>
            inner.RecordRecordsReadAsync(reportJobId, recordsRead, cancellationToken);
    }
}
