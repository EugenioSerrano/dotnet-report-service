using System.Net;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The priority a job is declared with, over the API's real pipeline: the three levels and
/// the default, the refusal of anything else, the deployment's restriction of <c>high</c> and
/// its refusal, the declared priority and the effective class the accepted job records, and
/// the idempotency rule applied to a request that differs only in its priority. Every refusal
/// must leave no job and no start request behind.
/// </summary>
public sealed class JobPriorityEndpointTests
{
    private const string Planted = "planted-secret-3f8a21";

    private static readonly Dictionary<string, string> AllowList = new(StringComparer.Ordinal)
    {
        [HighPriorityPolicy.SectionPath + ":" + HighPriorityPolicy.RestrictionSetting] = HighPriorityPolicy.AllowListRestriction,
        [HighPriorityPolicy.SectionPath + ":" + HighPriorityPolicy.AllowedConsumersSetting + ":0"] = JobApiHost.Consumer,
    };

    private static readonly Dictionary<string, string> Disabled = new(StringComparer.Ordinal)
    {
        [HighPriorityPolicy.SectionPath + ":" + HighPriorityPolicy.RestrictionSetting] = HighPriorityPolicy.DisabledRestriction,
    };

    [Theory]
    [InlineData("\"low\"", JobPriority.Low)]
    [InlineData("\"normal\"", JobPriority.Normal)]
    [InlineData("\"high\"", JobPriority.High)]
    [InlineData(null, JobPriority.Normal)]
    public async Task EachLevelAndTheOmittedMemberCreateAJobRecordingThatPriority(string? sent, JobPriority expected)
    {
        using var host = new JobApiHost();
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await SendAsync(host, JobApiHost.Consumer, JobApiHost.Body(uploadId, priority: sent));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = Assert.Single(host.Jobs.Jobs);
        Assert.Equal(expected, job.DeclaredPriority);
        Assert.Equal(expected, job.EffectivePriorityClass);

        // The answer of an acceptance is unchanged: its three members, no priority among them.
        Assert.Equal(3, (await JobApiHost.AnswerAsync(response)).EnumerateObject().Count());

        using var read = await host.Client.SendAsync(ReadRequest(job.ReportJobId), TestContext.Current.CancellationToken);
        var priority = (await JobApiHost.AnswerAsync(read)).GetProperty("priority");
        Assert.Equal(JobWireValues.ToWire(expected), priority.GetProperty("declared").GetString());
        Assert.Equal(JobWireValues.ToWire(expected), priority.GetProperty("effective").GetString());
    }

    [Theory]
    [InlineData("\"critical\"")]
    [InlineData("\"urgent\"")]
    [InlineData("\"\"")]
    [InlineData("\"HIGH\"")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task AnyOtherValueIsRefusedAsRequestInvalidNamingTheFieldAndCreatesNothing(string sent)
    {
        using var host = new JobApiHost();
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await SendAsync(host, JobApiHost.Consumer, JobApiHost.Body(uploadId, priority: sent));
        var problem = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.RequestInvalid, problem.GetProperty("code").GetString());
        Assert.Equal(CreateReportJobRequestReader.PriorityMember, problem.GetProperty("field").GetString());
        Assert.Empty(host.Jobs.Jobs);
        Assert.Empty(host.Jobs.Intents);
    }

    [Fact]
    public async Task ARefusedValueIsNeverEchoed()
    {
        using var host = new JobApiHost();
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await SendAsync(host, JobApiHost.Consumer, JobApiHost.Body(uploadId, priority: "\"" + Planted + "\""));
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(Planted, body, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Logs.Lines, line => line.Contains(Planted, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutARestrictionAnyDeclaredConsumerMayAskForHigh()
    {
        using var host = new JobApiHost();
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready, JobApiHost.OtherConsumer);

        using var response = await SendAsync(host, JobApiHost.OtherConsumer, JobApiHost.Body(uploadId, priority: "\"high\""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(JobPriority.High, Assert.Single(host.Jobs.Jobs).DeclaredPriority);
    }

    [Fact]
    public async Task AnAllowListAdmitsHighForTheConsumersItNames()
    {
        using var host = new JobApiHost(settings: AllowList);
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await SendAsync(host, JobApiHost.Consumer, JobApiHost.Body(uploadId, priority: "\"high\""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(JobPriority.High, Assert.Single(host.Jobs.Jobs).DeclaredPriority);
    }

    [Fact]
    public async Task AnAllowListRefusesHighForAnotherDeclaredConsumerWithUnsupportedCapabilityAndCreatesNothing()
    {
        using var host = new JobApiHost(settings: AllowList);
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready, JobApiHost.OtherConsumer);

        using var response = await SendAsync(host, JobApiHost.OtherConsumer, JobApiHost.Body(uploadId, priority: "\"high\""));
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var problem = await JobApiHost.AnswerAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodes.UnsupportedCapability, problem.GetProperty("code").GetString());
        Assert.NotEqual(ErrorCodes.ConsumerUnknown, problem.GetProperty("code").GetString());
        Assert.Equal(CreateReportJobEndpoint.HighPriorityNotOfferedDetail, problem.GetProperty("detail").GetString());
        Assert.Contains("'high'", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // Nothing of the policy is revealed: not its kind, not who it admits, not who asked.
        Assert.DoesNotContain(HighPriorityPolicy.AllowListRestriction, body, StringComparison.Ordinal);
        Assert.DoesNotContain(JobApiHost.Consumer, body, StringComparison.Ordinal);
        Assert.DoesNotContain(JobApiHost.OtherConsumer, body, StringComparison.Ordinal);

        Assert.Empty(host.Jobs.Jobs);
        Assert.Empty(host.Jobs.Intents);
        Assert.Contains(host.Logs.Lines, line => line.Contains("UNSUPPORTED_CAPABILITY (priority)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"low\"")]
    [InlineData("\"normal\"")]
    [InlineData(null)]
    public async Task AnAllowListLeavesTheOtherLevelsOpenToEveryDeclaredConsumer(string? sent)
    {
        using var host = new JobApiHost(settings: AllowList);
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready, JobApiHost.OtherConsumer);

        using var response = await SendAsync(host, JobApiHost.OtherConsumer, JobApiHost.Body(uploadId, priority: sent));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task ADisabledHighIsRefusedForEveryConsumerAndTheOtherLevelsStayOpen()
    {
        using var host = new JobApiHost(settings: Disabled);
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready);

        using (var refused = await SendAsync(host, JobApiHost.Consumer, JobApiHost.Body(uploadId, priority: "\"high\"")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal(ErrorCodes.UnsupportedCapability, (await JobApiHost.AnswerAsync(refused)).GetProperty("code").GetString());
            Assert.Empty(host.Jobs.Jobs);
        }

        using var accepted = await SendAsync(host, JobApiHost.Consumer, JobApiHost.Body(uploadId, priority: "\"low\""));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    [Fact]
    public async Task AnInvalidValueIsAnsweredBeforeTheRestriction()
    {
        using var host = new JobApiHost(settings: Disabled);
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready);

        using var response = await SendAsync(host, JobApiHost.Consumer, JobApiHost.Body(uploadId, priority: "\"urgent\""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnInactiveConsumerAskingForHighIsStillRefusedAsInactive()
    {
        using var host = new JobApiHost(settings: Disabled);

        using var response = await SendAsync(host, JobApiHost.InactiveConsumer, JobApiHost.Body("upload-0123456789abcdef0123456789abcdef", priority: "\"high\""));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.ConsumerInactive, (await JobApiHost.AnswerAsync(response)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("\"high\"", "\"high\"")]
    [InlineData(null, "\"normal\"")]
    [InlineData("\"normal\"", null)]
    public async Task TheSameKeyWithTheSamePriorityReturnsTheFirstJob(string? first, string? second)
    {
        using var host = new JobApiHost();
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready);

        using var one = await SendAsync(host, JobApiHost.Consumer, JobApiHost.Body(uploadId, priority: first), "key-0001");
        using var two = await SendAsync(host, JobApiHost.Consumer, JobApiHost.Body(uploadId, priority: second), "key-0001");

        Assert.Equal(HttpStatusCode.Accepted, one.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, two.StatusCode);
        Assert.Equal(
            (await JobApiHost.AnswerAsync(one)).GetProperty("reportJobId").GetString(),
            (await JobApiHost.AnswerAsync(two)).GetProperty("reportJobId").GetString());
        Assert.Single(host.Jobs.Jobs);
    }

    [Theory]
    [InlineData("\"high\"", "\"low\"")]
    [InlineData(null, "\"high\"")]
    [InlineData("\"low\"", null)]
    public async Task TheSameKeyWithAnotherPriorityIsAnIdempotencyConflictNotAReprioritisation(string? first, string? second)
    {
        using var host = new JobApiHost();
        var uploadId = await host.UploadAsync(JobApiHost.UploadState.Ready);

        using var one = await SendAsync(host, JobApiHost.Consumer, JobApiHost.Body(uploadId, priority: first), "key-0002");
        using var two = await SendAsync(host, JobApiHost.Consumer, JobApiHost.Body(uploadId, priority: second), "key-0002");

        Assert.Equal(HttpStatusCode.Accepted, one.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, two.StatusCode);
        Assert.Equal(ErrorCodes.IdempotencyConflict, (await JobApiHost.AnswerAsync(two)).GetProperty("code").GetString());
        var job = Assert.Single(host.Jobs.Jobs);
        Assert.Equal(
            RequestFingerprintEncoder.DeclaredPriority(new CreateReportJobRequest("t", null, "u", "p", ["pdf"], null, null, Level(first))),
            job.DeclaredPriority);
    }

    [Fact]
    public async Task ARetryOfAnAcceptedHighJobIsRefusedOnceTheDeploymentNoLongerOffersHigh()
    {
        // The restriction is decided with the other capabilities, before the key is looked up:
        // a deployment that stops offering high refuses the retry of a job it accepted earlier,
        // creates nothing, and leaves that first job as it was.
        using var before = new JobApiHost();
        var uploadId = await before.UploadAsync(JobApiHost.UploadState.Ready);
        using (var accepted = await SendAsync(before, JobApiHost.Consumer, JobApiHost.Body(uploadId, priority: "\"high\""), "key-0003"))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using var after = new JobApiHost(store: _ => before.Jobs, settings: Disabled);
        using var retry = await SendAsync(after, JobApiHost.Consumer, JobApiHost.Body(uploadId, priority: "\"high\""), "key-0003");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, retry.StatusCode);
        Assert.Equal(ErrorCodes.UnsupportedCapability, (await JobApiHost.AnswerAsync(retry)).GetProperty("code").GetString());
        var job = Assert.Single(before.Jobs.Jobs);
        Assert.Equal(JobPriority.High, job.DeclaredPriority);
    }

    private static string? Level(string? json) => json?.Trim('"');

    private static Task<HttpResponseMessage> SendAsync(JobApiHost host, string consumerId, string body, string? key = null)
    {
        var message = JobApiHost.CreateRequest(consumerId, body);
        if (key is not null)
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return host.Client.SendAsync(message, TestContext.Current.CancellationToken);
    }

    private static HttpRequestMessage ReadRequest(string reportJobId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}", UriKind.Relative));
        message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, JobApiHost.Consumer);
        return message;
    }
}
