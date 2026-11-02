using System.Net;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// Creating a job through the API's real pipeline: the accepted request with its
/// answer and the records it left behind, and every refusal the operation gives —
/// each of which must leave no job and no start request behind. The decisions of the
/// declared identity come before the body, the body before the capability, and the
/// capability before the data reference, so the answer of a request with several
/// faults is always the same one.
/// </summary>
public sealed class CreateReportJobEndpointTests : IDisposable
{
    private const string Planted = "planted-secret-3f8a21";

    private readonly JobApiHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task AReadyUploadCreatesAJobAndAnswersAcceptedWithItsIdentifierAndStatusUrl()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId)), cancellationToken);
        var answer = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var reportJobId = answer.GetProperty("reportJobId").GetString();
        Assert.NotNull(reportJobId);
        Assert.Matches("^job-[0-9a-f]{32}$", reportJobId);
        Assert.Equal("queued", answer.GetProperty("status").GetString());
        Assert.Equal($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}", answer.GetProperty("statusUrl").GetString());
        Assert.Equal(answer.GetProperty("statusUrl").GetString(), response.Headers.Location?.OriginalString);
        Assert.Equal(3, answer.EnumerateObject().Count());

        var job = Assert.Single(_host.Jobs.Jobs);
        Assert.Equal(reportJobId, job.ReportJobId);
        Assert.Equal(JobApiHost.Consumer, job.ConsumerId);
        Assert.Equal(uploadId, job.UploadId);
        Assert.Equal(ReportJobStatus.Queued, job.Status);
        Assert.Equal(JobWireValues.TruncateToMillisecond(JobApiHost.Now), job.ReportClock);
        Assert.Equal(JobWireValues.TruncateToMillisecond(JobApiHost.Now), job.CreatedAt);
        Assert.Null(job.Stage);

        var output = Assert.Single(_host.Jobs.OutputsOf(reportJobId!));
        Assert.Matches("^output-[0-9a-f]{32}$", output.OutputId);
        Assert.Equal(OutputFormat.Pdf, output.Format);
        Assert.Equal(OutputStatus.Pending, output.Status);

        var reference = _host.Jobs.ReferenceOf(reportJobId!);
        Assert.NotNull(reference);
        Assert.Equal("pilot-grouped", reference!.TemplateId);
        Assert.Equal(TemplateReferenceForm.Exact, reference.ReferenceForm);
        Assert.Equal("1.0.0", reference.RequestedVersion);

        var intent = Assert.Single(_host.Jobs.Intents);
        Assert.Equal(InMemoryJobStore.StartJobKind, intent.Kind);
        Assert.Equal(reportJobId, intent.ReportJobId);
        Assert.Equal(JobWireValues.TruncateToMillisecond(JobApiHost.Now), intent.RecordedAt);
        Assert.Contains(_host.Logs.Lines, line => line.Contains(reportJobId!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReferenceByNameIsStoredWithoutAVersion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId, version: null)), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var reference = _host.Jobs.ReferenceOf(Assert.Single(_host.Jobs.Jobs).ReportJobId);
        Assert.NotNull(reference);
        Assert.Equal(TemplateReferenceForm.ByName, reference!.ReferenceForm);
        Assert.Null(reference.RequestedVersion);
    }

    [Fact]
    public async Task AVerifyingUploadIsAccepted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Verifying);

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId)), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(_host.Jobs.Jobs);
    }

    [Fact]
    public async Task EveryUnusableDataReferenceIsRefusedWithTheSameProblemAndStoresNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var absent = "upload-ffffffffffffffffffffffffffffffff";
        var foreign = await _host.UploadAsync(JobApiHost.UploadState.Ready, "another-consumer");
        var malformed = "upload-not-an-identifier";
        var created = await _host.UploadAsync(JobApiHost.UploadState.Created);
        var uploading = await _host.UploadAsync(JobApiHost.UploadState.Uploading);
        var rejected = await _host.UploadAsync(JobApiHost.UploadState.Rejected);
        var expired = await _host.UploadAsync(JobApiHost.UploadState.Expired);
        var overdue = await _host.UploadAsync(JobApiHost.UploadState.VerifyingPastItsDeadline);

        var problems = new List<string>();
        var codes = new List<string?>();
        foreach (var uploadId in new[] { absent, foreign, malformed, created, uploading, rejected, expired, overdue })
        {
            using var response = await _host.Client.SendAsync(
                JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId)), cancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await JobApiHost.AnswerAsync(response);
            Assert.Equal("Upload not found", problem.GetProperty("title").GetString());
            codes.Add(problem.GetProperty("code").GetString());
            problems.Add(await JobApiHost.ProblemAsync(response));
        }

        Assert.Single(problems.Distinct(StringComparer.Ordinal));
        Assert.All(codes, code => Assert.Equal(ErrorCodes.InputNotFound, code));
        Assert.Empty(_host.Jobs.Jobs);
        Assert.Empty(_host.Jobs.Intents);
        Assert.Empty(_host.Jobs.Jobs);
        Assert.Empty(_host.Jobs.Intents);
    }

    [Fact]
    public async Task AnUploadOverdueButNotYetMarkedIsRefusedWithoutBeingChanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var overdue = await _host.UploadAsync(JobApiHost.UploadState.VerifyingPastItsDeadline);

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(overdue)), cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_host.Jobs.Jobs);
        var stored = await _host.Uploads.FindAsync(overdue, JobApiHost.Consumer, cancellationToken);
        Assert.Equal(UploadStatus.Verifying, stored!.Status);
    }

    [Theory]
    [InlineData("\"html\"", "header-items-v1")]
    [InlineData("\"xlsx\"", "header-items-v1")]
    [InlineData("\"markdown\"", "header-items-v1")]
    [InlineData("\"PDF\"", "header-items-v1")]
    [InlineData("\"\"", "header-items-v1")]
    [InlineData("\"pdf\", \"html\"", "header-items-v1")]
    [InlineData("\"pdf\"", "header-items-v2")]
    [InlineData("\"Pdf\"", "HEADER_ITEMS_V1")]
    public async Task AnUnsupportedOutputOrProfileIsRefusedSynchronouslyAndNothingIsStored(
        string outputs,
        string profile)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId, outputs: outputs, profile: profile)),
            cancellationToken);
        var problem = await JobApiHost.AnswerAsync(response);

        Assert.Equal(422, (int)response.StatusCode);
        Assert.Equal(ErrorCodes.UnsupportedCapability, problem.GetProperty("code").GetString());
        Assert.Equal("Unsupported capability", problem.GetProperty("title").GetString());
        Assert.Equal(0, _host.Jobs.AcceptCalls);
        Assert.Empty(_host.Jobs.Jobs);
    }

    [Fact]
    public async Task BothUnsupportedValuesAreRefusedInOneAnswerNamingTheFormatFirst()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(
                JobApiHost.Consumer,
                JobApiHost.Body(uploadId, outputs: "\"xlsx\"", profile: "root-array")),
            cancellationToken);
        var problem = await JobApiHost.AnswerAsync(response);

        Assert.Equal(422, (int)response.StatusCode);
        Assert.Equal(
            CreateReportJobEndpoint.UnsupportedOutputDetail + " " + CreateReportJobEndpoint.UnsupportedProfileDetail,
            problem.GetProperty("detail").GetString());
        Assert.Equal(0, _host.Jobs.AcceptCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dev consumer")]
    [InlineData("dev/consumer")]
    [InlineData("unknown-consumer")]
    public async Task AMissingMalformedOrUnregisteredIdentityIsRefusedIdenticallyAndTheBodyIsNotRead(
        string? identity)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);

        // A body the reader would refuse as too large: the identity is decided before it, so
        // the answer is the identity's and nothing else is touched.
        var oversized = JobApiHost.Body(uploadId).PadRight(CreateReportJobRequestReader.MaxBodyBytes + 1);
        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(identity, oversized), cancellationToken);
        var problem = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.ConsumerUnknown, problem.GetProperty("code").GetString());
        Assert.Equal("Unknown consumer", problem.GetProperty("title").GetString());
        Assert.Equal(0, _host.Jobs.AcceptCalls);
        Assert.Empty(_host.Jobs.Jobs);
    }

    [Fact]
    public async Task AnInactiveConsumerIsRefusedWithItsOwnCodeAndCreatesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready, JobApiHost.InactiveConsumer);

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.InactiveConsumer, JobApiHost.Body(uploadId)), cancellationToken);
        var problem = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.ConsumerInactive, problem.GetProperty("code").GetString());
        Assert.Equal("Inactive consumer", problem.GetProperty("title").GetString());
        Assert.Equal(CreateReportJobEndpoint.ConsumerInactiveDetail, problem.GetProperty("detail").GetString());
        Assert.Equal(0, _host.Jobs.AcceptCalls);
        Assert.Empty(_host.Jobs.Jobs);
    }

    [Fact]
    public async Task AMalformedRequestIsRefusedWithTheFieldPathAndTheReason()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var body = $$"""{"template":{"id":".{{Planted}}"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""";

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, body), cancellationToken);
        var problem = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.RequestInvalid, problem.GetProperty("code").GetString());
        Assert.Equal("Invalid request", problem.GetProperty("title").GetString());
        Assert.Equal("template.id", problem.GetProperty("field").GetString());
        Assert.Equal(RequestInvalidDetails.InvalidFormatReason, problem.GetProperty("reason").GetString());
        Assert.DoesNotContain(Planted, problem.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, _host.Jobs.AcceptCalls);
    }

    [Theory]
    [InlineData("\"render\":{\"culture\":\"en-GB\"}")]
    [InlineData("\"metadata\":{\"a\":\"b\"}")]
    public async Task AMemberThisOperationDoesNotAcceptIsRefused(string extra)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        var body = JobApiHost.Body(uploadId).Insert(1, extra + ",");

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, body), cancellationToken);
        var problem = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.RequestInvalid, problem.GetProperty("code").GetString());
        Assert.Equal(RequestInvalidDetails.UnknownFieldReason, problem.GetProperty("reason").GetString());
        Assert.Equal(1, problem.GetProperty("unknownFieldCount").GetInt32());
        Assert.Equal(0, _host.Jobs.AcceptCalls);
    }

    [Fact]
    public async Task AnIdempotencyKeyHeaderIsIgnoredAndTheJobIsCreated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        using var request = JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId));
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "3f8a21-plated");

        using var response = await _host.Client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(_host.Jobs.Jobs);
        Assert.Single(_host.Jobs.Intents);
    }

    [Fact]
    public async Task AClientThatLeavesAfterItsBodyDoesNotCancelTheJob()
    {
        using var cancellation = new CancellationTokenSource();
        using var host = new JobApiHost(inner => new CancellingJobStore(inner, cancellation));
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready);
        using var request = JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId));

        try
        {
            using var response = await host.Client.SendAsync(request, cancellation.Token);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            // The answer could not be delivered because the client had left; the job stands.
        }

        var job = Assert.Single(host.Jobs.Jobs);
        Assert.Equal(ReportJobStatus.Queued, job.Status);
        Assert.Single(host.Jobs.Intents);
        Assert.Contains(host.Logs.Lines, line => line.Contains(job.ReportJobId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStoreThatCannotBeReachedIsRefusedWithServiceUnavailableAndStoresNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = new JobApiHost(_ => new UnavailableJobStore());
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId)), cancellationToken);
        var problem = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ErrorCodes.ServiceUnavailable, problem.GetProperty("code").GetString());
        Assert.Equal("Service unavailable", problem.GetProperty("title").GetString());
        Assert.Empty(host.Jobs.Jobs);
    }

    // Cancels the client's request from inside the acceptance, after the job is durable.
    private sealed class CancellingJobStore(IJobStore inner, CancellationTokenSource cancellation) : IJobStore
    {
        public async Task<JobAcceptanceResult> AcceptAsync(
            JobAcceptance acceptance,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var result = await inner.AcceptAsync(acceptance, now, cancellationToken);
            await cancellation.CancelAsync();
            return result;
        }

        public Task<JobStateRecord?> FindForWorkflowAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.FindForWorkflowAsync(reportJobId, cancellationToken);

        public Task<JobFailureOutcome> FailJobAsync(
            string reportJobId,
            string code,
            string reason,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            inner.FailJobAsync(reportJobId, code, reason, now, cancellationToken);
    }

    // Stands in for records that cannot be reached at all.
    private sealed class UnavailableJobStore : IJobStore
    {
        public Task<JobAcceptanceResult> AcceptAsync(
            JobAcceptance acceptance,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new JobStoreUnavailableException("08006", new TimeoutException("the server did not answer"));

        public Task<JobStateRecord?> FindForWorkflowAsync(string reportJobId, CancellationToken cancellationToken) =>
            throw new JobStoreUnavailableException("08006", new TimeoutException("the server did not answer"));

        public Task<JobFailureOutcome> FailJobAsync(
            string reportJobId,
            string code,
            string reason,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new JobStoreUnavailableException("08006", new TimeoutException("the server did not answer"));
    }
}
