using System.Net;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.TemplatePackages.Manifest;
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

    // The header the client names its request by, sent as the wire spells it.
    private static HttpRequestMessage WithKey(HttpRequestMessage message, string key)
    {
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return message;
    }

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
    [InlineData("\"render\":{\"paper\":\"a4\"}")]
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

    [Theory]
    [InlineData("""{"culture":"xx-notreal"}""", "render.culture", RequestedLocale.UnsupportedCultureDetail)]
    [InlineData("""{"timeZone":"Mars/Olympus"}""", "render.timeZone", RequestedLocale.UnsupportedTimeZoneDetail)]
    public async Task APresentationTheRuntimeDoesNotHaveIsRefusedAtOnceAndNothingIsStored(
        string render,
        string field,
        string detail)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        var body = JobApiHost.Body(uploadId, render: render);

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, body), cancellationToken);
        var problem = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.RequestInvalid, problem.GetProperty("code").GetString());
        Assert.Equal(RequestInvalidDetails.Title, problem.GetProperty("title").GetString());
        Assert.Equal(field, problem.GetProperty("field").GetString());
        Assert.Equal(RequestInvalidDetails.InvalidFormatReason, problem.GetProperty("reason").GetString());
        Assert.Equal(detail, problem.GetProperty("detail").GetString());

        // No job was created, and neither the job store nor the taken versions were touched.
        Assert.Equal(0, _host.Jobs.AcceptCalls);
        Assert.Equal(0, _host.Versions.FindCalls);
        Assert.Equal(0, _host.Versions.ListCalls);
        Assert.Empty(_host.Jobs.Jobs);
        Assert.Empty(_host.Jobs.Intents);
        Assert.DoesNotContain("xx-notreal", problem.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Mars/Olympus", problem.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APresentationIsDecidedAfterTheCapabilityAndBeforeTheReferenceSyntax()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        const string render = """{"culture":"xx-notreal"}""";

        // A presentation that cannot be honoured is refused before any template or data work:
        // after the capability, which is about whether this deployment can serve the request at
        // all, and before the reference's syntax, which is about the template.
        using var capability = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(
                JobApiHost.Consumer,
                JobApiHost.Body(uploadId, outputs: "\"xlsx\"", render: render)),
            cancellationToken);
        using var reference = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(
                JobApiHost.Consumer,
                JobApiHost.Body(uploadId, version: "^1.0.0", render: render)),
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, capability.StatusCode);
        Assert.Equal(
            ErrorCodes.UnsupportedCapability,
            (await JobApiHost.AnswerAsync(capability)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, reference.StatusCode);
        Assert.Equal(
            ErrorCodes.RequestInvalid,
            (await JobApiHost.AnswerAsync(reference)).GetProperty("code").GetString());
        Assert.Empty(_host.Jobs.Jobs);
    }

    [Fact]
    public async Task ARequestThatNamesItsPresentationStoresItWithTheJobAndItsReference()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(
                JobApiHost.Consumer,
                JobApiHost.Body(
                    uploadId,
                    render: """{"culture":"es-AR","timeZone":"America/Argentina/Buenos_Aires"}""")),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = Assert.Single(_host.Jobs.Jobs);
        Assert.Equal("es-AR", job.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", job.TimeZone);

        // The step's own read carries the presentation with the reference, in one round trip.
        var reference = _host.Jobs.ReferenceOf(job.ReportJobId);
        Assert.NotNull(reference);
        Assert.Equal("es-AR", reference!.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", reference.TimeZone);
        Assert.Null(reference.Pin);
    }

    [Fact]
    public async Task ARequestWithoutAPresentationStoresNone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId)), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = Assert.Single(_host.Jobs.Jobs);
        Assert.Null(job.Culture);
        Assert.Null(job.TimeZone);
        var reference = _host.Jobs.ReferenceOf(job.ReportJobId);
        Assert.Null(reference!.Culture);
        Assert.Null(reference.TimeZone);
    }

    [Fact]
    public async Task APresentationIsStoredInTheSpellingTheRequestUsed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(
                JobApiHost.Consumer,
                JobApiHost.Body(uploadId, render: """{"culture":"en-us","timeZone":"UTC"}""")),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = Assert.Single(_host.Jobs.Jobs);

        // A tag is case-insensitive to every reader, so the service keeps the spelling it was
        // given rather than inventing one of its own.
        Assert.Equal("en-us", job.Culture);
        Assert.Equal("UTC", job.TimeZone);
    }

    [Fact]
    public async Task ARepeatedRequestWithTheSameKeyAnswersTheFirstJobAndWritesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        var body = JobApiHost.Body(uploadId);

        using var first = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, body), "K1"), cancellationToken);
        var firstAnswer = await JobApiHost.AnswerAsync(first);
        var firstBody = await first.Content.ReadAsStringAsync(cancellationToken);
        var versionReads = _host.Versions.FindCalls + _host.Versions.ListCalls;
        var writes = _host.Jobs.WriteCalls;

        using var second = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, body), "K1"), cancellationToken);
        var secondAnswer = await JobApiHost.AnswerAsync(second);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(firstAnswer.GetProperty("reportJobId").GetString(), secondAnswer.GetProperty("reportJobId").GetString());
        Assert.Equal(firstAnswer.GetProperty("statusUrl").GetString(), secondAnswer.GetProperty("statusUrl").GetString());
        Assert.Equal(first.Headers.Location?.OriginalString, second.Headers.Location?.OriginalString);
        Assert.Equal(firstBody, await second.Content.ReadAsStringAsync(cancellationToken));

        Assert.Single(_host.Jobs.Jobs);
        Assert.Single(_host.Jobs.Intents);
        Assert.Equal(versionReads, _host.Versions.FindCalls + _host.Versions.ListCalls);
        Assert.Equal(writes, _host.Jobs.WriteCalls);
        var fingerprint = _host.Jobs.FingerprintOf(firstAnswer.GetProperty("reportJobId").GetString()!)
            ?? throw new Xunit.Sdk.XunitException("the accepted job records no request identity");
        Assert.True(RequestFingerprint.IsWellFormed(fingerprint.Value));
    }

    [Fact]
    public async Task AUsedKeyWithADifferentRequestIsRefusedWithAConflictAndLeavesTheFirstJobUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        var other = await _host.UploadAsync(JobApiHost.UploadState.Ready);

        using var accepted = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(first)), "K1"), cancellationToken);
        var jobId = (await JobApiHost.AnswerAsync(accepted)).GetProperty("reportJobId").GetString();
        var reference = _host.Jobs.ReferenceOf(jobId!);
        var writes = _host.Jobs.WriteCalls;

        using var conflict = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(other)), "K1"), cancellationToken);
        var problem = await JobApiHost.AnswerAsync(conflict);

        // The same key naming an upload this consumer does not have is still the key's answer:
        // the conflict, never a missing upload.
        using var absent = await _host.Client.SendAsync(
            WithKey(
                JobApiHost.CreateRequest(
                    JobApiHost.Consumer,
                    JobApiHost.Body("upload-ffffffffffffffffffffffffffffffff")),
                "K1"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, absent.StatusCode);
        Assert.Equal("IDEMPOTENCY_CONFLICT", problem.GetProperty("code").GetString());
        Assert.Equal("Idempotency key conflict", problem.GetProperty("title").GetString());
        Assert.Equal("The idempotency key was already used with a different request.", problem.GetProperty("detail").GetString());
        Assert.DoesNotContain("K1", await conflict.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);

        Assert.Single(_host.Jobs.Jobs);
        Assert.Single(_host.Jobs.Intents);
        Assert.Equal(writes, _host.Jobs.WriteCalls);
        Assert.Equal(reference, _host.Jobs.ReferenceOf(jobId!));
    }

    [Fact]
    public async Task RequestsWithoutAKeyCreateOneJobEach()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        var body = JobApiHost.Body(uploadId);

        using var first = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, body), cancellationToken);
        using var second = await _host.Client.SendAsync(
            JobApiHost.CreateRequest(JobApiHost.Consumer, body), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(2, _host.Jobs.Jobs.Count);
        Assert.All(
            _host.Jobs.Jobs,
            job => Assert.True(RequestFingerprint.IsWellFormed(
                (_host.Jobs.FingerprintOf(job.ReportJobId)
                    ?? throw new Xunit.Sdk.XunitException("an accepted job records no request identity")).Value)));
    }

    [Fact]
    public async Task AKeyBelongsToTheConsumerThatUsedIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = new JobApiHost(registry: new ReplaceableConfiguration(RegistryMap(
            ("consumer-a", true),
            ("consumer-b", true))));
        var uploadA = await host.UploadAsync(JobApiHost.UploadState.Ready, "consumer-a");
        var uploadB = await host.UploadAsync(JobApiHost.UploadState.Ready, "consumer-b");

        using var acceptedByA = await host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest("consumer-a", JobApiHost.Body(uploadA)), "K1"), cancellationToken);
        var jobA = (await JobApiHost.AnswerAsync(acceptedByA)).GetProperty("reportJobId").GetString();

        using var acceptedByB = await host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest("consumer-b", JobApiHost.Body(uploadB)), "K1"), cancellationToken);
        var jobB = (await JobApiHost.AnswerAsync(acceptedByB)).GetProperty("reportJobId").GetString();

        Assert.Equal(HttpStatusCode.Accepted, acceptedByA.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, acceptedByB.StatusCode);
        Assert.NotEqual(jobA, jobB);
        Assert.Equal(2, host.Jobs.Jobs.Count);

        using var repeatA = await host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest("consumer-a", JobApiHost.Body(uploadA)), "K1"), cancellationToken);
        using var repeatB = await host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest("consumer-b", JobApiHost.Body(uploadB)), "K1"), cancellationToken);

        Assert.Equal(jobA, (await JobApiHost.AnswerAsync(repeatA)).GetProperty("reportJobId").GetString());
        Assert.Equal(jobB, (await JobApiHost.AnswerAsync(repeatB)).GetProperty("reportJobId").GetString());
        Assert.Equal(2, host.Jobs.Jobs.Count);
    }

    [Fact]
    public async Task AByNameRepeatKeepsTheFirstJobsVersionAfterANewerOneIsTaken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        var body = JobApiHost.Body(uploadId, version: null);
        _host.Versions.Seed(TemplateVersionRecords.Sample(
            templateId: "pilot-grouped", version: "1.0.0", owner: JobApiHost.Consumer, shared: true));

        using var first = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, body), "K1"), cancellationToken);
        var jobId = (await JobApiHost.AnswerAsync(first)).GetProperty("reportJobId").GetString();
        var reference = _host.Jobs.ReferenceOf(jobId!);
        Assert.Equal("1.0.0", reference!.BaselineVersion);
        var versionReads = _host.Versions.ListCalls;

        _host.Versions.Seed(TemplateVersionRecords.Sample(
            templateId: "pilot-grouped", version: "1.1.0", owner: "another-consumer", shared: false));
        _host.Versions.Seed(TemplateVersionRecords.Sample(
            templateId: "pilot-grouped", version: "1.1.0", owner: JobApiHost.Consumer, shared: true));

        using var repeat = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, body), "K1"), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, repeat.StatusCode);
        Assert.Equal(jobId, (await JobApiHost.AnswerAsync(repeat)).GetProperty("reportJobId").GetString());
        Assert.Equal(versionReads, _host.Versions.ListCalls);
        Assert.Equal(reference, _host.Jobs.ReferenceOf(jobId!));
        Assert.Single(_host.Jobs.Jobs);

        using var newer = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, body), "K2"), cancellationToken);
        var newerId = (await JobApiHost.AnswerAsync(newer)).GetProperty("reportJobId").GetString();
        Assert.NotEqual(jobId, newerId);
        Assert.Equal(2, _host.Jobs.Jobs.Count);
        Assert.Equal("1.1.0", _host.Jobs.ReferenceOf(newerId!)!.BaselineVersion);
    }

    [Fact]
    public async Task ARequestRefusedForItsOwnReasonIsAnsweredSoUnderABoundKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        using var accepted = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId)), "K1"), cancellationToken);
        var jobId = (await JobApiHost.AnswerAsync(accepted)).GetProperty("reportJobId").GetString();

        using var unsupported = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId, outputs: "\"html\"")), "K1"),
            cancellationToken);
        using var unknownConsumer = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest("not-registered", JobApiHost.Body(uploadId)), "K1"), cancellationToken);
        using var malformed = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, "{"), "K1"), cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsupported.StatusCode);
        Assert.Equal(ErrorCodes.UnsupportedCapability, (await JobApiHost.AnswerAsync(unsupported)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, unknownConsumer.StatusCode);
        Assert.Equal(ErrorCodes.ConsumerUnknown, (await JobApiHost.AnswerAsync(unknownConsumer)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(ErrorCodes.RequestInvalid, (await JobApiHost.AnswerAsync(malformed)).GetProperty("code").GetString());
        Assert.Single(_host.Jobs.Jobs);
        Assert.Equal(jobId, _host.Jobs.Jobs[0].ReportJobId);
    }

    [Fact]
    public async Task ARepeatIsAnsweredAfterTheFirstJobsUploadWasRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Verifying);
        using var accepted = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId)), "K1"), cancellationToken);
        var jobId = (await JobApiHost.AnswerAsync(accepted)).GetProperty("reportJobId").GetString();

        await _host.Uploads.RejectTransferAsync(
            uploadId,
            UploadRejectionCode.DataInvalid,
            UploadRejectionReason.Truncated,
            12,
            JobApiHost.Now.AddMinutes(1),
            cancellationToken);

        using var repeat = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId)), "K1"), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, repeat.StatusCode);
        Assert.Equal(jobId, (await JobApiHost.AnswerAsync(repeat)).GetProperty("reportJobId").GetString());
        Assert.Single(_host.Jobs.Jobs);
    }

    [Fact]
    public async Task AMalformedKeyIsRefusedBeforeAnyRecordIsTouched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        var body = JobApiHost.Body(uploadId);

        foreach (var key in new[] { "has space", "café", new string('K', 256) })
        {
            using var response = await _host.Client.SendAsync(
                WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, body), key), cancellationToken);
            var problem = await JobApiHost.AnswerAsync(response);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("REQUEST_INVALID", problem.GetProperty("code").GetString());
            Assert.Equal("Idempotency-Key", problem.GetProperty("field").GetString());
            Assert.Equal(
                "The header 'Idempotency-Key' must be 1 to 255 visible ASCII characters, sent once.",
                problem.GetProperty("detail").GetString());
            Assert.DoesNotContain(key, await response.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        Assert.Empty(_host.Jobs.Jobs);
        Assert.Equal(0, _host.Jobs.AcceptCalls);
    }

    [Fact]
    public async Task AnUnreadableKeyStoreAnswersServiceUnavailableAndStoresNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = new JobApiHost(store: _ => new UnavailableJobStore());
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId)), "K1"), cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            ErrorCodes.ServiceUnavailable,
            (await JobApiHost.AnswerAsync(response)).GetProperty("code").GetString());
        Assert.Empty(host.Jobs.Jobs);
        Assert.Empty(host.Jobs.Intents);
    }

    [Fact]
    public async Task NeitherTheKeyNorTheIdentityAppearsInALogOrAnAnswer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready);
        var plantedKey = Planted;

        using var accepted = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId)), plantedKey), cancellationToken);
        var jobId = (await JobApiHost.AnswerAsync(accepted)).GetProperty("reportJobId").GetString();
        var fingerprint = _host.Jobs.FingerprintOf(jobId!);

        using var repeat = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(uploadId)), plantedKey), cancellationToken);
        using var conflict = await _host.Client.SendAsync(
            WithKey(JobApiHost.CreateRequest(JobApiHost.Consumer, JobApiHost.Body(
                await _host.UploadAsync(JobApiHost.UploadState.Ready))), plantedKey), cancellationToken);

        foreach (var answer in new[] { accepted, repeat, conflict })
        {
            var body = await answer.Content.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain(plantedKey, body, StringComparison.Ordinal);
            Assert.DoesNotContain(fingerprint!.Value.Value, body, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(_host.Logs.Lines, line => line.Contains(plantedKey, StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Logs.Lines, line => line.Contains(fingerprint!.Value.Value, StringComparison.Ordinal));
    }

    private static Dictionary<string, string?> RegistryMap(params (string ConsumerId, bool Active)[] consumers)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < consumers.Length; index++)
        {
            settings[$"consumers:{index}:consumerId"] = consumers[index].ConsumerId;
            settings[$"consumers:{index}:active"] = consumers[index].Active ? "true" : "false";
        }

        return settings;
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

        public Task<IdempotencyLookup> FindByIdempotencyKeyAsync(
            string consumerId,
            string idempotencyKey,
            RequestFingerprint fingerprint,
            CancellationToken cancellationToken) =>
            inner.FindByIdempotencyKeyAsync(consumerId, idempotencyKey, fingerprint, cancellationToken);

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
            CancellationToken cancellationToken,
            TemplateLocaleDefaults? presentation) =>
            inner.PinTemplateAsync(reportJobId, pin, now, cancellationToken, presentation);

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

        public Task<JobArtifactsView?> ListArtifactsAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) =>
            inner.ListArtifactsAsync(reportJobId, consumerId, cancellationToken);

        public Task<ArtifactView?> FindArtifactAsync(string reportJobId, string artifactId, string consumerId, CancellationToken cancellationToken) =>
            inner.FindArtifactAsync(reportJobId, artifactId, consumerId, cancellationToken);

        public Task<OutputFailureOutcome> FailOutputAsync(string outputId, string code, string? reason, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FailOutputAsync(outputId, code, reason, now, cancellationToken);

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
    {        public Task<JobAcceptanceResult> AcceptAsync(
            JobAcceptance acceptance,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new JobStoreUnavailableException("08006", new TimeoutException("the server did not answer"));

        public Task<IdempotencyLookup> FindByIdempotencyKeyAsync(
            string consumerId,
            string idempotencyKey,
            RequestFingerprint fingerprint,
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
            CancellationToken cancellationToken,
            TemplateLocaleDefaults? presentation) =>
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

        public Task<JobArtifactsView?> ListArtifactsAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) => throw Unreachable();

        public Task<ArtifactView?> FindArtifactAsync(string reportJobId, string artifactId, string consumerId, CancellationToken cancellationToken) => throw Unreachable();

        public Task<OutputFailureOutcome> FailOutputAsync(string outputId, string code, string? reason, DateTimeOffset now, CancellationToken cancellationToken) => throw Unreachable();

        public Task<JobFinalizationResult> FinalizeJobAsync(string reportJobId, DateTimeOffset now, CancellationToken cancellationToken) => throw Unreachable();

        private static JobStoreUnavailableException Unreachable() =>
            new("08006", new TimeoutException("the server did not answer"));
    }
}
