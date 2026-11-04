using System.Net;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.TemplatePackages.Records;
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
        var body = $$"""{"template":{"id":17},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""";

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, body), cancellationToken);
        var problem = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.RequestInvalid, problem.GetProperty("code").GetString());
        Assert.Equal("Invalid request", problem.GetProperty("title").GetString());
        Assert.Equal("template.id", problem.GetProperty("field").GetString());
        Assert.Equal(RequestInvalidDetails.WrongTypeReason, problem.GetProperty("reason").GetString());
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

    [Theory]
    [InlineData("Pilot-Grouped", "1.0.0")]
    [InlineData("pilot grouped", "1.0.0")]
    [InlineData("pilot/grouped", "1.0.0")]
    [InlineData("invoice-energy", "1.0")]
    [InlineData("invoice-energy", "v1.0.0")]
    [InlineData("invoice-energy", "01.0.0")]
    [InlineData("invoice-energy", "^1.0.0")]
    [InlineData("invoice-energy", "1.x")]
    [InlineData("invoice-energy", "main")]
    [InlineData("invoice-energy", "latest")]
    public async Task AReferenceOutsideTheSyntaxIsRefusedAsNotFoundWithoutReadingAnyStore(
        string templateId,
        string version)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        var planted = "planted-secret-3f8a21";
        var body = $"{{\"template\":{{\"id\":\"{templateId}-{planted}\",\"version\":\"{version}\"}},"
            + $"\"data\":{{\"uploadId\":\"{uploadId}\",\"profile\":\"header-items-v1\"}},\"outputs\":[\"pdf\"]}}";

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, body), cancellationToken);
        var problem = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.TemplateNotFound, problem.GetProperty("code").GetString());
        Assert.Equal(TemplateReferenceSyntax.NotFoundTitle, problem.GetProperty("title").GetString());
        Assert.Equal(TemplateReferenceSyntax.NotFoundDetail, problem.GetProperty("detail").GetString());
        Assert.Empty(_host.Jobs.Jobs);
        Assert.Empty(_host.Jobs.Intents);
        Assert.Equal(0, _host.Versions.FindCalls);
        Assert.Equal(0, _host.Versions.ListCalls);
        Assert.DoesNotContain(_host.Logs.Lines, line => line.Contains(planted, StringComparison.Ordinal) || line.Contains(version, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AForeignTakenVersionIsRefusedWithTheSameProblemAsAReferenceThatCannotBeUsed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        _host.Versions.Seed(TemplateVersionRecords.Sample(templateId: "pilot-grouped", version: "1.1.0", owner: "another-consumer", shared: false));

        using var foreign = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId, version: "1.1.0")), cancellationToken);
        using var syntax = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId, version: "^1.0.0")), cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await JobApiHost.ProblemAsync(syntax), await JobApiHost.ProblemAsync(foreign));
        var answer = await JobApiHost.AnswerAsync(foreign);
        Assert.Equal(ErrorCodes.TemplateNotFound, answer.GetProperty("code").GetString());
        Assert.Empty(_host.Jobs.Jobs);
        Assert.Empty(_host.Jobs.Intents);
    }

    [Fact]
    public async Task AnUntakenExactVersionIsAcceptedEvenWhenAnotherConsumerOwnsNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId, version: "9.9.9")), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(_host.Jobs.Jobs);
    }

    [Fact]
    public async Task ASharedTwinOfAForeignTakenVersionIsAccepted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        _host.Versions.Seed(TemplateVersionRecords.Sample(templateId: "pilot-grouped", version: "1.2.0", owner: "another-consumer", shared: true));

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId, version: "1.2.0")), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(_host.Jobs.Jobs);
    }

    [Theory]
    [InlineData("another-consumer")]
    [InlineData("dev-consumer")]
    public async Task AnUntakenExactVersionIsAcceptedForTheTakeToDecide(string owner)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);

        // A record this consumer may not use, of another version: it says nothing about this one.
        _host.Versions.Seed(TemplateVersionRecords.Sample(templateId: "pilot-grouped", version: "0.9.0", owner: owner, shared: false));

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId, version: "1.1.0")), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var reference = _host.Jobs.ReferenceOf(Assert.Single(_host.Jobs.Jobs).ReportJobId);
        Assert.Equal(TemplateReferenceForm.Exact, reference!.ReferenceForm);
        Assert.Null(reference.BaselineVersion);
    }

    [Fact]
    public async Task AReferenceByNameRecordsTheHighestUsableReleaseAsItsBaseline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        _host.Versions.Seed(TemplateVersionRecords.Sample(templateId: "pilot-grouped", version: "1.0.0", owner: JobApiHost.Consumer));
        _host.Versions.Seed(TemplateVersionRecords.Sample(templateId: "pilot-grouped", version: "1.1.0", owner: JobApiHost.Consumer));
        _host.Versions.Seed(TemplateVersionRecords.Sample(templateId: "pilot-grouped", version: "1.10.0", owner: JobApiHost.Consumer));
        _host.Versions.Seed(TemplateVersionRecords.Sample(templateId: "pilot-grouped", version: "2.0.0-beta.1", owner: JobApiHost.Consumer));

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId, version: null)), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var reference = _host.Jobs.ReferenceOf(Assert.Single(_host.Jobs.Jobs).ReportJobId);
        Assert.NotNull(reference);
        Assert.Equal(TemplateReferenceForm.ByName, reference!.ReferenceForm);
        Assert.Null(reference.RequestedVersion);
        Assert.Equal("1.10.0", reference.BaselineVersion);
    }

    [Fact]
    public async Task AReferenceByNameWithNothingUsableTakenRecordsNoBaseline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        _host.Versions.Seed(TemplateVersionRecords.Sample(templateId: "pilot-grouped", version: "2.0.0-beta.1", owner: JobApiHost.Consumer));

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId, version: null)), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null(_host.Jobs.ReferenceOf(Assert.Single(_host.Jobs.Jobs).ReportJobId)!.BaselineVersion);
    }

    [Fact]
    public async Task TheTakenVersionReadThatCannotBeReachedAnswersServiceUnavailableAndStoresNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = new JobApiHost(versions: _ => new UnavailableTakenVersionStore());
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId)), cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await JobApiHost.AnswerAsync(response);
        Assert.Equal(ErrorCodes.ServiceUnavailable, problem.GetProperty("code").GetString());
        Assert.Empty(host.Jobs.Jobs);
        Assert.Equal(0, host.Jobs.AcceptCalls);
    }

    [Fact]
    public async Task TheReferenceSyntaxIsDecidedBeforeTheDataReferenceAndTheCapabilityBeforeEither()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var absentUpload = "upload-ffffffffffffffffffffffffffffffff";
        var foreignVersion = $"{{\"template\":{{\"id\":\"invoice-energy\",\"version\":\"^1.0.0\"}},"
            + $"\"data\":{{\"uploadId\":\"{absentUpload}\",\"profile\":\"header-items-v1\"}},\"outputs\":[\"pdf\"]}}";
        var unsupported = $"{{\"template\":{{\"id\":\"invoice-energy\",\"version\":\"^1.0.0\"}},"
            + $"\"data\":{{\"uploadId\":\"{absentUpload}\",\"profile\":\"header-items-v1\"}},\"outputs\":[\"xlsx\"]}}";

        using var reference = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, foreignVersion), cancellationToken);
        using var capability = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, unsupported), cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, reference.StatusCode);
        Assert.Equal(ErrorCodes.TemplateNotFound, (await JobApiHost.AnswerAsync(reference)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, capability.StatusCode);
        Assert.Equal(ErrorCodes.UnsupportedCapability, (await JobApiHost.AnswerAsync(capability)).GetProperty("code").GetString());
        Assert.Empty(_host.Jobs.Jobs);
    }

    [Theory]
    [InlineData("""{"template":{"version":"1.0.0"},"data":{"uploadId":"upload-00000000000000000000000000000001","profile":"header-items-v1"},"outputs":["pdf"]}""")]
    [InlineData("""{"template":{"id":"invoice-energy","version":null},"data":{"uploadId":"upload-00000000000000000000000000000001","profile":"header-items-v1"},"outputs":["pdf"]}""")]
    [InlineData("""{"template":{"id":"invoice-energy","version":1},"data":{"uploadId":"upload-00000000000000000000000000000001","profile":"header-items-v1"},"outputs":["pdf"]}""")]
    public async Task AStructuralFaultIsStillAMalformedRequest(string body)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, body), cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.RequestInvalid, (await JobApiHost.AnswerAsync(response)).GetProperty("code").GetString());
        Assert.Empty(_host.Jobs.Jobs);
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

        public Task<TemplateReferenceRecord?> FindTemplateReferenceAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.FindTemplateReferenceAsync(reportJobId, cancellationToken);

        public Task<JobFailureOutcome> FailJobAsync(
            string reportJobId,
            string code,
            string reason,
            string? message,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            inner.FailJobAsync(reportJobId, code, reason, message, now, cancellationToken);

        public Task<TemplatePinState> PinTemplateAsync(
            string reportJobId,
            TemplatePin pin,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            inner.PinTemplateAsync(reportJobId, pin, now, cancellationToken);

        public Task<TransitionJobResult> TransitionJobAsync(
            string reportJobId,
            IReadOnlyList<ReportJobStatus> from,
            ReportJobStatus to,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            inner.TransitionJobAsync(reportJobId, from, to, now, cancellationToken);

        public Task<ReportJobStatusView?> FindStatusAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) =>
            inner.FindStatusAsync(reportJobId, consumerId, cancellationToken);

        public Task<ProgressWriteOutcome> RecordRecordsReadAsync(string reportJobId, long recordsRead, CancellationToken cancellationToken) =>
            inner.RecordRecordsReadAsync(reportJobId, recordsRead, cancellationToken);

        public Task<TransitionOutcome> TransitionOutputAsync(string outputId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.TransitionOutputAsync(outputId, now, cancellationToken);

        public Task<BeginInputWaitOutcome> BeginInputWaitAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.BeginInputWaitAsync(reportJobId, cancellationToken);

        public Task<EndInputWaitOutcome> EndInputWaitAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.EndInputWaitAsync(reportJobId, cancellationToken);

        public Task<IReadOnlyList<string>> FindInputWaitingJobIdsAsync(string uploadId, CancellationToken cancellationToken) =>
            inner.FindInputWaitingJobIdsAsync(uploadId, cancellationToken);

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
    }

    // Stands in for the taken versions when the store cannot be reached at all.
    private sealed class UnavailableTakenVersionStore : ITemplateVersionStore
    {
        public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken) =>
            throw new TemplateStoreUnavailableException(new TimeoutException("the store did not answer"));

        public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) =>
            throw new TemplateStoreUnavailableException(new TimeoutException("the store did not answer"));

        public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken) =>
            throw new TemplateStoreUnavailableException(new TimeoutException("the store did not answer"));
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

        public Task<TemplateReferenceRecord?> FindTemplateReferenceAsync(string reportJobId, CancellationToken cancellationToken) =>
            throw new JobStoreUnavailableException("08006", new TimeoutException("the server did not answer"));

        public Task<JobFailureOutcome> FailJobAsync(
            string reportJobId,
            string code,
            string reason,
            string? message,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new JobStoreUnavailableException("08006", new TimeoutException("the server did not answer"));

        public Task<TemplatePinState> PinTemplateAsync(
            string reportJobId,
            TemplatePin pin,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new JobStoreUnavailableException("08006", new TimeoutException("the server did not answer"));

        public Task<TransitionJobResult> TransitionJobAsync(
            string reportJobId,
            IReadOnlyList<ReportJobStatus> from,
            ReportJobStatus to,
            DateTimeOffset now,
            CancellationToken cancellationToken) => throw Unreachable();

        public Task<ReportJobStatusView?> FindStatusAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) => throw Unreachable();

        public Task<ProgressWriteOutcome> RecordRecordsReadAsync(string reportJobId, long recordsRead, CancellationToken cancellationToken) => throw Unreachable();

        public Task<TransitionOutcome> TransitionOutputAsync(string outputId, DateTimeOffset now, CancellationToken cancellationToken) => throw Unreachable();

        public Task<BeginInputWaitOutcome> BeginInputWaitAsync(string reportJobId, CancellationToken cancellationToken) => throw Unreachable();

        public Task<EndInputWaitOutcome> EndInputWaitAsync(string reportJobId, CancellationToken cancellationToken) => throw Unreachable();

        public Task<IReadOnlyList<string>> FindInputWaitingJobIdsAsync(string uploadId, CancellationToken cancellationToken) => throw Unreachable();

        public Task<PartitionCommitResult> CommitPartitionAsync(PartitionRecord partition, CancellationToken cancellationToken) => throw Unreachable();

        public Task<PartitionRecord?> FindPartitionAsync(string reportJobId, CancellationToken cancellationToken) => throw Unreachable();

        public Task<PartCommitResult> CommitPartAsync(PartCommit commit, CancellationToken cancellationToken) => throw Unreachable();

        public Task<PartCommit?> FindPartAsync(string outputId, int blockIndex, CancellationToken cancellationToken) => throw Unreachable();

        public Task<PublishResult> PublishAsync(ArtifactRecord artifact, CancellationToken cancellationToken) => throw Unreachable();

        public Task<OutputFailureOutcome> FailOutputAsync(string outputId, string code, DateTimeOffset now, CancellationToken cancellationToken) => throw Unreachable();

        public Task<JobFinalizationResult> FinalizeJobAsync(string reportJobId, DateTimeOffset now, CancellationToken cancellationToken) => throw Unreachable();

        private static JobStoreUnavailableException Unreachable() =>
            new("08006", new TimeoutException("the server did not answer"));
    }
}
