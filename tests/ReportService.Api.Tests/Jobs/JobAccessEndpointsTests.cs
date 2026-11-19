using System.Net;
using System.Text.Json;
using ReportService.Api.Artifacts;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Telemetry;
using ReportService.Api.Uploads;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Testing;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The three job reads through the API's real pipeline, as one rule: a job's status, its
/// artifact list and its artifact download are answered only to the consumer that owns
/// them, a job or file of another consumer is answered exactly as one that does not exist
/// — code, members, headers and bytes, apart from the trace identifier — an identity that
/// is missing, malformed, unregistered or a case variant of a registered one is refused
/// with one <c>CONSUMER_UNKNOWN</c> answer that touches no record and no object and is
/// counted once with its operation and identity state, and an inactive consumer keeps every
/// answer it had while it was active. The cases seed their records through the stores' own
/// writers and read them over HTTP only.
/// </summary>
public sealed class JobAccessEndpointsTests : IAsyncLifetime, IDisposable
{
    /// <summary>A value a request may declare that is not a registered consumer and must never be logged.</summary>
    private const string UnregisteredIdentity = "planted-marker-4f1c";

    private static readonly DateTimeOffset Now = JobWireValues.TruncateToMillisecond(JobApiHost.Now);

    /// <summary>Every identity state the gate decides, with the state the answer must carry.</summary>
    private static readonly (string Name, string?[]? Values, string State)[] Identities =
    [
        ("absent", null, "missing"),
        ("empty", [""], "missing"),
        ("repeated", [JobAccessFixture.ConsumerA, JobAccessFixture.ConsumerB], "malformed"),
        ("comma-joined", [JobAccessFixture.ConsumerA + "," + JobAccessFixture.ConsumerB], "malformed"),
        ("malformed", ["consumer a"], "malformed"),
        ("too-long", [new string('a', 129)], "malformed"),
        ("unregistered", [UnregisteredIdentity], "unregistered"),
        ("case-variant", ["Consumer-A"], "unregistered"),
    ];

    private readonly ReplaceableConfiguration _registry = new(RegistryMap(
        (JobAccessFixture.ConsumerA, true),
        (JobAccessFixture.ConsumerB, true),
        (JobAccessFixture.ConsumerC, false)));

    /// <summary>The meter this case's host publishes on, so its listener hears no neighbour's refusals.</summary>
    private readonly string _meterName = "reportservice-job-access-" + Guid.NewGuid().ToString("N");

    private readonly RefusalInstruments _refusals;
    private readonly JobApiHost _host;
    private ObservedStorage _storage = null!;
    private JobAccessFixture _fixture = null!;

    /// <summary>Starts the API over the fixture's registry and counts what it opens and writes.</summary>
    public JobAccessEndpointsTests()
    {
        _refusals = new RefusalInstruments(_meterName);
        _host = new JobApiHost(
            objects: inner => _storage = new ObservedStorage(inner),
            registry: _registry,
            meterName: _meterName);
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _fixture = new JobAccessFixture(_host.Jobs, _host.Uploads, _storage, Now);
        await _fixture.SeedAsync(cancellationToken);

        // The gone case is the live artifact of a succeeded job whose stored object is deleted
        // through the port, so the row still names bytes no reader can find.
        var gone = _fixture.Gone.Artifact!;
        Assert.True((await _storage.DeleteAsync(ObjectKey.Create(gone.ObjectKey), cancellationToken)).Succeeded);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _host.Dispose();
        _refusals.Dispose();
    }

    /// <summary>Releases the asynchronous lifetime; every resource is released by <see cref="Dispose"/>.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AnUnknownIdentityIsRefusedOnTheThreeReadsAndCountedOnceWithItsOperationAndState()
    {
        var answers = new List<ProblemAnswer.Captured>();
        var targets = new[]
        {
            (JobId: _fixture.Succeeded.JobId, ArtifactId: (string?)_fixture.Succeeded.Artifact!.ArtifactId),
            (JobId: JobAccessFixture.AbsentJobId, ArtifactId: (string?)JobAccessFixture.AbsentArtifactId),
            (JobId: JobAccessFixture.MalformedJobIds[0], ArtifactId: (string?)JobAccessFixture.MalformedArtifactIds[0]),
        };

        foreach (var (name, values, state) in Identities)
        {
            foreach (var (jobId, artifactId) in targets)
            {
                foreach (var operation in Operations(jobId, artifactId!))
                {
                    _refusals.Reset();
                    var statusCalls = _host.Jobs.FindStatusCalls;
                    var listCalls = _host.Jobs.ListArtifactCalls;
                    var artifactCalls = _host.Jobs.FindArtifactCalls;
                    var opens = _storage.Opens;
                    var writes = _storage.Writes;
                    var logStart = _host.Logs.Lines.Count;

                    using var response = await SendAsync(values, HttpMethod.Get, operation.Path);
                    var captured = await ProblemAnswer.CaptureAsync(response);

                    Assert.True(
                        HttpStatusCode.Forbidden == response.StatusCode,
                        $"{name} on {operation.Name} of {jobId} had to be refused");
                    var problem = JsonDocument.Parse(captured.Body).RootElement;
                    Assert.Equal(ErrorCodes.ConsumerUnknown, problem.GetProperty("code").GetString());
                    Assert.Equal(ConsumerAccess.UnknownTitle, problem.GetProperty("title").GetString());
                    Assert.Equal(
                        new[] { "type", "title", "status", "detail", "code", "traceId" },
                        problem.EnumerateObject().Select(member => member.Name).ToArray());
                    AssertNoIdentifierInHeaders(response, values, operation.JobId, operation.ArtifactId);

                    Assert.Equal(statusCalls, _host.Jobs.FindStatusCalls);
                    Assert.Equal(listCalls, _host.Jobs.ListArtifactCalls);
                    Assert.Equal(artifactCalls, _host.Jobs.FindArtifactCalls);
                    Assert.Equal(opens, _storage.Opens);
                    Assert.Equal(writes, _storage.Writes);
                    Assert.DoesNotContain(
                        _host.Logs.Lines.Skip(logStart),
                        line => UnregisteredIdentity.Length > 0 && line.Contains(UnregisteredIdentity, StringComparison.Ordinal));

                    var measurement = Assert.Single(_refusals.Measurements);
                    Assert.Equal(ErrorCodes.ConsumerUnknown, measurement.Code);
                    Assert.Equal(operation.Operation, measurement.Operation);
                    Assert.Equal(state, measurement.IdentityState);

                    answers.Add(captured);
                }
            }
        }

        foreach (var answer in answers.Skip(1))
        {
            ProblemAnswer.AssertEqual(answers[0], answer);
        }

        // One refusal for the whole API: the upload status read's refusal is the same answer.
        using var upload = await SendAsync(
            null,
            HttpMethod.Get,
            $"{UploadEndpoints.RoutePrefix}/upload-ffffffffffffffffffffffffffffffff");
        ProblemAnswer.AssertEqual(answers[0], await ProblemAnswer.CaptureAsync(upload));
    }

    [Fact]
    public async Task AnUnknownIdentityOnAMalformedIdentifierIsRefusedBeforeTheGrammarChecks()
    {

        foreach (var malformed in JobAccessFixture.MalformedJobIds)
        {
            using var response = await SendAsync([UnregisteredIdentity], HttpMethod.Get, StatusPath(malformed));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(
                ErrorCodes.ConsumerUnknown,
                (await JobApiHost.AnswerAsync(response)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task AForeignJobIsAnsweredAsAnAbsentOneOnTheStatusReadAndTheList()
    {
        var reference = await ReferenceJobAnswersAsync(JobAccessFixture.ConsumerB);
        var writesBefore = _host.Jobs.WriteCalls;
        var opensBefore = _storage.Opens;
        var refusalsBefore = _refusals.Count;

        foreach (var job in new[] { _fixture.Queued, _fixture.Running, _fixture.Failed, _fixture.Succeeded, _fixture.Expired })
        {
            var callsBefore = _host.Jobs.FindStatusCalls;
            using var status = await SendAsync([JobAccessFixture.ConsumerB], HttpMethod.Get, StatusPath(job.JobId));
            Assert.Equal(callsBefore + 1, _host.Jobs.FindStatusCalls);
            var statusAnswer = await ProblemAnswer.CaptureAsync(status);
            Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
            ProblemAnswer.AssertEqual(reference.JobNotFound, statusAnswer);

            var listCallsBefore = _host.Jobs.ListArtifactCalls;
            using var list = await SendAsync([JobAccessFixture.ConsumerB], HttpMethod.Get, ListPath(job.JobId));
            Assert.Equal(listCallsBefore + 1, _host.Jobs.ListArtifactCalls);
            var listAnswer = await ProblemAnswer.CaptureAsync(list);
            Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
            ProblemAnswer.AssertEqual(reference.JobNotFound, listAnswer);
        }

        Assert.Equal(writesBefore, _host.Jobs.WriteCalls);
        Assert.Equal(opensBefore, _storage.Opens);
        Assert.Equal(refusalsBefore, _refusals.Count);
    }

    [Fact]
    public async Task AForeignArtifactIsAnsweredAsAnAbsentOneAndNeverAsExpiredOrUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reference = await ReferenceArtifactAnswerAsync(JobAccessFixture.ConsumerB);
        var opensBefore = _storage.Opens;
        var writesBefore = _host.Jobs.WriteCalls;
        var refusalsBefore = _refusals.Count;

        var combinations = new[]
        {
            (_fixture.Succeeded.JobId, _fixture.Succeeded.Artifact!.ArtifactId),
            (_fixture.Expired.JobId, _fixture.Expired.Artifact!.ArtifactId),
            (_fixture.Gone.JobId, _fixture.Gone.Artifact!.ArtifactId),
            (_fixture.SucceededOfB.JobId, _fixture.Succeeded.Artifact!.ArtifactId),
            (_fixture.Succeeded.JobId, _fixture.SucceededOfB.Artifact!.ArtifactId),
        };

        foreach (var (jobId, artifactId) in combinations)
        {
            using var response = await SendAsync(
                [JobAccessFixture.ConsumerB],
                HttpMethod.Get,
                DownloadPath(jobId, artifactId));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var answer = await ProblemAnswer.CaptureAsync(response);
            var problem = JsonDocument.Parse(answer.Body).RootElement;
            Assert.Equal(ErrorCodes.InputNotFound, problem.GetProperty("code").GetString());
            Assert.Equal(ArtifactDownloadEndpoint.NotFoundTitle, problem.GetProperty("title").GetString());
            Assert.Equal(ArtifactDownloadEndpoint.NotFoundDetail, problem.GetProperty("detail").GetString());
            ProblemAnswer.AssertEqual(reference, answer);
            Assert.True(response.StatusCode != HttpStatusCode.Gone, "an expiry of another consumer's artifact must not be confirmed");
            Assert.True(response.StatusCode != HttpStatusCode.InternalServerError, "a missing object of another consumer's artifact must not be a fault");
        }

        Assert.Equal(opensBefore, _storage.Opens);
        Assert.Equal(writesBefore, _host.Jobs.WriteCalls);
        Assert.Equal(refusalsBefore, _refusals.Count);

        // Nothing of consumer A changed: its row is still the owner's and its object is still there.
        Assert.NotNull(await _host.Jobs.FindArtifactAsync(
            _fixture.Succeeded.JobId,
            _fixture.Succeeded.Artifact!.ArtifactId,
            JobAccessFixture.ConsumerA,
            cancellationToken));
        Assert.True((await _storage.GetInfoAsync(
            ObjectKey.Create(_fixture.Succeeded.Artifact!.ObjectKey),
            cancellationToken)).Succeeded);
    }

    [Fact]
    public async Task TheOwnersAnswersStayAsDelivered()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using (var status = await SendAsync([JobAccessFixture.ConsumerA], HttpMethod.Get, StatusPath(_fixture.Succeeded.JobId)))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            var answer = await JobApiHost.AnswerAsync(status);
            Assert.Equal("succeeded", answer.GetProperty("status").GetString());
        }

        using (var list = await SendAsync([JobAccessFixture.ConsumerA], HttpMethod.Get, ListPath(_fixture.Succeeded.JobId)))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var listed = Assert.Single((await JobApiHost.AnswerAsync(list)).GetProperty("artifacts").EnumerateArray());
            Assert.Equal(_fixture.Succeeded.Artifact!.ArtifactId, listed.GetProperty("artifactId").GetString());
        }

        using (var download = await DownloadAsync([JobAccessFixture.ConsumerA], _fixture.Succeeded.JobId, _fixture.Succeeded.Artifact!.ArtifactId))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            var installed = await download.Content.ReadAsByteArrayAsync(cancellationToken);
            Assert.Equal(_fixture.Succeeded.Artifact.SizeBytes, installed.LongLength);
            Assert.Equal(_fixture.Succeeded.Artifact.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(installed)));
        }

        using (var expired = await DownloadAsync([JobAccessFixture.ConsumerA], _fixture.Expired.JobId, _fixture.Expired.Artifact!.ArtifactId))
        {
            Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
            Assert.Equal(
                ErrorCodes.ArtifactExpired,
                (await JobApiHost.AnswerAsync(expired)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task AnInactiveConsumerKeepsEveryAnswerAndItsOwnResults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var active = await CaptureOwnerAnswersAsync(JobAccessFixture.ConsumerA);
        var fingerprint = await FingerprintAsync();

        _registry.Replace(RegistryMap(
            (JobAccessFixture.ConsumerA, false),
            (JobAccessFixture.ConsumerB, true),
            (JobAccessFixture.ConsumerC, false)));
        Assert.NotEqual(fingerprint, await FingerprintAsync());

        var refusalsBefore = _refusals.Count;
        var inactive = await CaptureOwnerAnswersAsync(JobAccessFixture.ConsumerA);

        Assert.Equal(active.StatusBody, inactive.StatusBody);
        Assert.Equal(active.ListBody, inactive.ListBody);
        Assert.Equal(active.Download, inactive.Download);
        ProblemAnswer.AssertEqual(active.Expired, inactive.Expired);
        ProblemAnswer.AssertEqual(active.Absent, inactive.Absent);
        ProblemAnswer.AssertEqual(active.Foreign, inactive.Foreign);
        Assert.Equal(refusalsBefore, _refusals.Count);

        // The consumer that was inactive from the start reads, lists and downloads its own job.
        using (var status = await SendAsync([JobAccessFixture.ConsumerC], HttpMethod.Get, StatusPath(_fixture.SucceededOfC.JobId)))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            Assert.Equal("succeeded", (await JobApiHost.AnswerAsync(status)).GetProperty("status").GetString());
        }

        using (var list = await SendAsync([JobAccessFixture.ConsumerC], HttpMethod.Get, ListPath(_fixture.SucceededOfC.JobId)))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Single((await JobApiHost.AnswerAsync(list)).GetProperty("artifacts").EnumerateArray());
        }

        using (var download = await DownloadAsync(
            [JobAccessFixture.ConsumerC],
            _fixture.SucceededOfC.JobId,
            _fixture.SucceededOfC.Artifact!.ArtifactId))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            var installed = await download.Content.ReadAsByteArrayAsync(cancellationToken);
            Assert.Equal(_fixture.SucceededOfC.Artifact.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(installed)));
        }
    }

    [Fact]
    public async Task AConsumerRemovedAndReAddedIsRefusedAndThenReadsItsDataAgain()
    {
        var active = await CaptureOwnerAnswersAsync(JobAccessFixture.ConsumerA);
        var fingerprint = await FingerprintAsync();

        _registry.Replace(RegistryMap(
            (JobAccessFixture.ConsumerB, true),
            (JobAccessFixture.ConsumerC, false)));
        Assert.NotEqual(fingerprint, await FingerprintAsync());

        var missingIdentity = await ProblemAnswer.CaptureAsync(
            await SendAsync(null, HttpMethod.Get, StatusPath(_fixture.Succeeded.JobId)));
        _refusals.Reset();
        ProblemAnswer.Captured removed;
        using (var status = await SendAsync([JobAccessFixture.ConsumerA], HttpMethod.Get, StatusPath(_fixture.Succeeded.JobId)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, status.StatusCode);
            removed = await ProblemAnswer.CaptureAsync(status);
            Assert.Equal(
                ErrorCodes.ConsumerUnknown,
                JsonDocument.Parse(removed.Body).RootElement.GetProperty("code").GetString());
        }

        ProblemAnswer.AssertEqual(missingIdentity, removed);

        var measurement = Assert.Single(_refusals.Measurements);
        Assert.Equal(ConsumerRefusalMetrics.JobStatusOperation, measurement.Operation);
        Assert.Equal("unregistered", measurement.IdentityState);

        var removedFingerprint = await FingerprintAsync();
        _registry.Replace(RegistryMap(
            (JobAccessFixture.ConsumerA, true),
            (JobAccessFixture.ConsumerB, true),
            (JobAccessFixture.ConsumerC, false)));
        Assert.NotEqual(removedFingerprint, await FingerprintAsync());

        var again = await CaptureOwnerAnswersAsync(JobAccessFixture.ConsumerA);
        Assert.Equal(active.StatusBody, again.StatusBody);
        Assert.Equal(active.ListBody, again.ListBody);
        Assert.Equal(active.Download, again.Download);
        ProblemAnswer.AssertEqual(active.Expired, again.Expired);
    }

    [Fact]
    public async Task IdentifiersThatDifferOnlyByCaseOrSuffixAreIsolatedFromEachOther()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string caseVariant = "Consumer-A";
        const string suffixVariant = "consumer-ab";

        _registry.Replace(RegistryMap(
            (JobAccessFixture.ConsumerA, true),
            (caseVariant, true),
            (suffixVariant, true)));

        var caseJob = await _fixture.AddSucceededAsync(caseVariant, cancellationToken);
        var suffixJob = await _fixture.AddSucceededAsync(suffixVariant, cancellationToken);
        var own = new[]
        {
            (Consumer: JobAccessFixture.ConsumerA, Job: _fixture.Succeeded),
            (Consumer: caseVariant, Job: caseJob),
            (Consumer: suffixVariant, Job: suffixJob),
        };

        var reference = await ReferenceJobAnswersAsync(JobAccessFixture.ConsumerA);
        var absentArtifact = await ReferenceArtifactAnswerAsync(JobAccessFixture.ConsumerA);
        foreach (var (consumer, job) in own)
        {
            using (var status = await SendAsync([consumer], HttpMethod.Get, StatusPath(job.JobId)))
            {
                Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            }

            using (var list = await SendAsync([consumer], HttpMethod.Get, ListPath(job.JobId)))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            }

            using (var download = await DownloadAsync([consumer], job.JobId, job.Artifact!.ArtifactId))
            {
                Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            }

            foreach (var (otherConsumer, otherJob) in own.Where(entry => !string.Equals(entry.Consumer, consumer, StringComparison.Ordinal)))
            {
                using var status = await SendAsync([consumer], HttpMethod.Get, StatusPath(otherJob.JobId));
                Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
                ProblemAnswer.AssertEqual(reference.JobNotFound, await ProblemAnswer.CaptureAsync(status));

                using var list = await SendAsync([consumer], HttpMethod.Get, ListPath(otherJob.JobId));
                Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
                ProblemAnswer.AssertEqual(reference.JobNotFound, await ProblemAnswer.CaptureAsync(list));

                using var download = await DownloadAsync([consumer], otherJob.JobId, otherJob.Artifact!.ArtifactId);
                Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
                ProblemAnswer.AssertEqual(absentArtifact, await ProblemAnswer.CaptureAsync(download));
            }
        }
    }

    private IReadOnlyList<(string Name, string Operation, string Path, string JobId, string? ArtifactId)> Operations(
        string jobId,
        string artifactId) =>
    [
        ("status", ConsumerRefusalMetrics.JobStatusOperation, StatusPath(jobId), jobId, null),
        ("list", ConsumerRefusalMetrics.ArtifactListOperation, ListPath(jobId), jobId, null),
        (
            "download",
            ConsumerRefusalMetrics.ArtifactDownloadOperation,
            DownloadPath(jobId, artifactId),
            jobId,
            artifactId),
    ];

    private Task<HttpResponseMessage> SendAsync(string?[]? identity, HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (identity is not null)
        {
            foreach (var value in identity)
            {
                message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, value);
            }
        }

        return _host.Client.SendAsync(message, TestContext.Current.CancellationToken);
    }

    private Task<HttpResponseMessage> DownloadAsync(string?[]? identity, string jobId, string artifactId)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, new Uri(DownloadPath(jobId, artifactId), UriKind.Relative));
        if (identity is not null)
        {
            foreach (var value in identity)
            {
                message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, value);
            }
        }

        return _host.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
    }

    private async Task<JobAnswers> ReferenceJobAnswersAsync(string consumer)
    {
        var notFound = new List<ProblemAnswer.Captured>();
        using (var absent = await SendAsync([consumer], HttpMethod.Get, StatusPath(JobAccessFixture.AbsentJobId)))
        {
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
            notFound.Add(await ProblemAnswer.CaptureAsync(absent));
        }

        using (var malformed = await SendAsync([consumer], HttpMethod.Get, StatusPath(JobAccessFixture.MalformedJobIds[0])))
        {
            Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
            notFound.Add(await ProblemAnswer.CaptureAsync(malformed));
        }

        var refused = await ProblemAnswer.CaptureAsync(await SendAsync(null, HttpMethod.Get, StatusPath(_fixture.Succeeded.JobId)));
        ProblemAnswer.AssertEqual(notFound[0], notFound[1]);
        return new JobAnswers(notFound[0], refused);
    }

    private async Task<ProblemAnswer.Captured> ReferenceArtifactAnswerAsync(string consumer)
    {
        using var absent = await DownloadAsync(
            [consumer],
            JobAccessFixture.AbsentJobId,
            JobAccessFixture.AbsentArtifactId);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        var absentAnswer = await ProblemAnswer.CaptureAsync(absent);

        using var malformed = await DownloadAsync(
            [consumer],
            JobAccessFixture.MalformedJobIds[0],
            JobAccessFixture.MalformedArtifactIds[0]);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        var malformedAnswer = await ProblemAnswer.CaptureAsync(malformed);

        ProblemAnswer.AssertEqual(absentAnswer, malformedAnswer);
        return absentAnswer;
    }

    private async Task<OwnerAnswers> CaptureOwnerAnswersAsync(string consumer)
    {
        var statusBody = await ReadBodyAsync(await SendAsync([consumer], HttpMethod.Get, StatusPath(_fixture.Succeeded.JobId)));
        var listBody = await ReadBodyAsync(await SendAsync([consumer], HttpMethod.Get, ListPath(_fixture.Succeeded.JobId)));

        byte[] download;
        using (var response = await DownloadAsync([consumer], _fixture.Succeeded.JobId, _fixture.Succeeded.Artifact!.ArtifactId))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            download = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        }

        var expired = await ProblemAnswer.CaptureAsync(
            await DownloadAsync([consumer], _fixture.Expired.JobId, _fixture.Expired.Artifact!.ArtifactId));
        var absent = await ProblemAnswer.CaptureAsync(
            await SendAsync([consumer], HttpMethod.Get, StatusPath(JobAccessFixture.AbsentJobId)));
        var foreign = await ProblemAnswer.CaptureAsync(
            await SendAsync([consumer], HttpMethod.Get, StatusPath(_fixture.SucceededOfB.JobId)));

        return new OwnerAnswers(statusBody, listBody, download, expired, absent, foreign);
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }
    }

    private async Task<string> FingerprintAsync()
    {
        using var response = await _host.Client.GetAsync(
            new Uri(ConsumerRegistryHosting.DiagnosticPath, UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.GetProperty("checks")[0].GetProperty("data").GetProperty("fingerprint").GetString()!;
    }

    private static void AssertNoIdentifierInHeaders(
        HttpResponseMessage response,
        string?[]? identity,
        string jobId,
        string? artifactId)
    {
        var values = response.Headers.Concat(response.Content.Headers)
            .SelectMany(header => header.Value)
            .ToArray();
        foreach (var known in new[] { jobId, artifactId }.Concat(identity ?? []).OfType<string>())
        {
            if (known.Length == 0)
            {
                continue;
            }

            Assert.DoesNotContain(values, value => value.Contains(known, StringComparison.Ordinal));
        }
    }

    private static string StatusPath(string jobId) => $"{CreateReportJobEndpoint.RoutePrefix}/{jobId}";

    private static string ListPath(string jobId) => $"{StatusPath(jobId)}{ArtifactListEndpoint.RouteSuffix}";

    private static string DownloadPath(string jobId, string artifactId) =>
        $"{ListPath(jobId)}/{artifactId}{ArtifactDownloadEndpoint.RouteSuffix}";

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

    /// <summary>The reference answers of a consumer's job reads.</summary>
    private sealed record JobAnswers(ProblemAnswer.Captured JobNotFound, ProblemAnswer.Captured Refused);

    /// <summary>One consumer's answers before and after a registry change.</summary>
    private sealed record OwnerAnswers(
        string StatusBody,
        string ListBody,
        byte[] Download,
        ProblemAnswer.Captured Expired,
        ProblemAnswer.Captured Absent,
        ProblemAnswer.Captured Foreign);

}
