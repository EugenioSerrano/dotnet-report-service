using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Dapper;
using Npgsql;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Uploads;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// Creating a report job on the running composition: a ready upload of a consumer the
/// temporary registry holds is accepted with <c>202 queued</c> and its status URL, and
/// the job's execution starts once — read from the composition's own database, because
/// the status read is a later operation; every request the service cannot serve is
/// refused with its code and leaves the job count where it was.
/// </summary>
public sealed class ReportJobAcceptanceEndToEndTests : IDisposable
{
    private const string Registry = """
        { "consumers": [
            { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true },
            { "consumerId": "consumer-b", "displayName": "Consumer B", "active": true },
            { "consumerId": "consumer-c", "displayName": "Consumer C", "active": false } ] }
        """;

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan DispatchDeadline = TimeSpan.FromMinutes(2);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public ReportJobAcceptanceEndToEndTests() => Directory.CreateDirectory(_directory);

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task AReadyUploadIsAcceptedQueuedAndEveryUnservableRequestIsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (app, api) = await StartAsync(cancellationToken);
        await using var composition = app;
        var connectionString = await app.GetConnectionStringAsync("reportservice", cancellationToken)
            ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");

        var uploadId = await StreamAndConfirmAsync(api, ConsumerA(), cancellationToken);
        Assert.Equal("ready", await WaitForUploadStatusAsync(api, ConsumerA(), uploadId, cancellationToken));

        var before = await JobCountAsync(connectionString, cancellationToken);
        using (var accepted = await api.SendAsync(JobRequest(ConsumerA(), uploadId, ["pdf"]), cancellationToken))
        {
            var body = await accepted.Content.ReadAsStringAsync(cancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            using var document = JsonDocument.Parse(body);
            var reportJobId = document.RootElement.GetProperty("reportJobId").GetString()!;
            Assert.Matches("^job-[0-9a-f]{32}$", reportJobId);
            Assert.Equal("queued", document.RootElement.GetProperty("status").GetString());
            Assert.Equal($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}", accepted.Headers.Location!.OriginalString);
            Assert.Equal(
                accepted.Headers.Location.OriginalString,
                document.RootElement.GetProperty("statusUrl").GetString());

            // The job is durable and its execution starts exactly once, which is what the
            // composition's own records can show while no status read exists.
            var job = await JobAsync(connectionString, reportJobId, cancellationToken);
            Assert.NotNull(job);
            Assert.Equal("QUEUED", job!.Status);
            Assert.Equal(uploadId, job.UploadId);
            Assert.Equal(ConsumerA(), job.ConsumerId);
        }

        Assert.Equal(before + 1, await JobCountAsync(connectionString, cancellationToken));
        await WaitForDispatchAsync(connectionString, cancellationToken);

        var after = await JobCountAsync(connectionString, cancellationToken);

        // A format this increment does not produce.
        using (var refused = await api.SendAsync(JobRequest(ConsumerA(), uploadId, ["html"]), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal(ErrorCodes.UnsupportedCapability, await CodeAsync(refused, cancellationToken));
        }

        // An upload that was never confirmed.
        var unconfirmed = await CreateUploadAsync(api, ConsumerA(), cancellationToken);
        using (var refused = await api.SendAsync(JobRequest(ConsumerA(), unconfirmed, ["pdf"]), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.Equal(ErrorCodes.InputNotFound, await CodeAsync(refused, cancellationToken));
        }

        // An identity the registry does not hold, and one it holds as inactive.
        using (var refused = await api.SendAsync(JobRequest("consumer-z", uploadId, ["pdf"]), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(ErrorCodes.ConsumerUnknown, await CodeAsync(refused, cancellationToken));
        }

        using (var refused = await api.SendAsync(JobRequest("consumer-c", uploadId, ["pdf"]), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(ErrorCodes.ConsumerInactive, await CodeAsync(refused, cancellationToken));
        }

        Assert.Equal(after, await JobCountAsync(connectionString, cancellationToken));
    }

    private async Task<(DistributedApplication App, HttpClient Api)> StartAsync(CancellationToken cancellationToken)
    {
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(registry, Registry, cancellationToken);
        var appHost = await TestComposition.CreateAsync(cancellationToken, $"--Consumers:File={registry}");
        var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(HealthyDeadline);
            await app.ResourceNotifications.WaitForResourceHealthyAsync("api", deadline.Token);
        }

        return (app, app.CreateHttpClient("api"));
    }

    private static string ConsumerA() => "consumer-a";

    private static async Task<string> StreamAndConfirmAsync(HttpClient api, string consumer, CancellationToken cancellationToken)
    {
        var uploadId = await CreateUploadAsync(api, consumer, cancellationToken);
        var body = Encoding.UTF8.GetBytes("""{"header":{},"items":[{"id":1,"v":17}]}""");
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(CreateUploadRequestReader.JsonMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/content", UriKind.Relative))
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumer);
        using (var sent = await api.SendAsync(request, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        using var confirmed = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/complete", UriKind.Relative))
            {
                Content = new StringContent("""{"sha256":null}""", Encoding.UTF8, "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, consumer } },
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        return uploadId;
    }

    private static async Task<string> CreateUploadAsync(HttpClient api, string consumer, CancellationToken cancellationToken)
    {
        using var created = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
            {
                Content = new StringContent("""{"profile":"header-items-v1","mode":"API_STREAM"}""", Encoding.UTF8, "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, consumer } },
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("uploadId").GetString()!;
    }

    private static async Task<string?> WaitForUploadStatusAsync(
        HttpClient api,
        string consumer,
        string uploadId,
        CancellationToken cancellationToken)
    {
        string? status = null;
        await Wait.UntilAsync(
            $"the upload {uploadId} to end verified",
            async token =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}", UriKind.Relative));
                request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumer);
                using var response = await api.SendAsync(request, token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return false;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                status = document.RootElement.GetProperty("status").GetString();
                return status is "ready" or "rejected";
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return status;
    }

    private static HttpRequestMessage JobRequest(string consumer, string uploadId, string[] outputs)
    {
        var requested = string.Join(',', outputs.Select(output => $"\"{output}\""));
        var body = $$"""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":[{{requested}}]}""";
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumer);
        return request;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("code").GetString();
    }

    private static async Task<long> JobCountAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM report_service.report_jobs",
            cancellationToken: cancellationToken));
    }

    private static async Task<JobRow?> JobAsync(string connectionString, string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(
            """
            SELECT report_job_id AS "ReportJobId", consumer_id AS "ConsumerId", upload_id AS "UploadId", status AS "Status"
              FROM report_service.report_jobs
             WHERE report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private static Task WaitForDispatchAsync(string connectionString, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            "the accepted job's execution to be recorded",
            async token =>
            {
                await using var connection = new NpgsqlConnection(connectionString);
                var dispatched = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    """
                    SELECT count(*)
                      FROM report_service.intents i
                      JOIN report_service.execution_references r ON r.intent_id = i.intent_id
                     WHERE i.kind = 'start-job' AND i.status = 'DISPATCHED'
                    """,
                    cancellationToken: token));
                return dispatched > 0;
            },
            DispatchDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

    /// <summary>One job row, as the composition's database holds it.</summary>
    private sealed class JobRow
    {
        public string ReportJobId { get; set; } = string.Empty;

        public string ConsumerId { get; set; } = string.Empty;

        public string UploadId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;
    }
}

/// <summary>
/// The second half of the same story on a build that can print: the job accepted on the
/// running composition reaches a terminal status, <c>SUCCEEDED</c> for these inputs. It
/// carries the trait that keeps it out of a run that cannot print, and when the steps
/// that print are not on the build it runs on, it fails by name instead of passing as a
/// covering run that never covered anything.
/// </summary>
[Trait("Requires", "printing")]
public sealed class ReportJobCompletionEndToEndTests : IDisposable
{
    /// <summary>The message a run on a build without the printing steps fails with.</summary>
    public const string PrintingNotOnThisCommit =
        "printing is not on this commit: the accepted job's execution ended with the job still queued, so the steps that take a ready input to its end are not part of this build, and this run cannot be a covering run.";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan TerminalDeadline = TimeSpan.FromMinutes(5);

    private const string Registry = """
        { "consumers": [
            { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true },
            { "consumerId": "consumer-b", "displayName": "Consumer B", "active": true },
            { "consumerId": "consumer-c", "displayName": "Consumer C", "active": false } ] }
        """;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public ReportJobCompletionEndToEndTests() => Directory.CreateDirectory(_directory);

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task TheAcceptedJobReachesItsTerminalStatus()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(registry, Registry, cancellationToken);
        var appHost = await TestComposition.CreateAsync(cancellationToken, $"--Consumers:File={registry}");
        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(HealthyDeadline);
            await app.ResourceNotifications.WaitForResourceHealthyAsync("api", deadline.Token);
        }

        var connectionString = await app.GetConnectionStringAsync("reportservice", cancellationToken)
            ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");
        using var api = app.CreateHttpClient("api");
        var uploadId = await ReadyUploadAsync(api, cancellationToken);

        using var accepted = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
            {
                Content = new StringContent(
                    $$"""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":["pdf"]}""",
                    Encoding.UTF8,
                    "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, "consumer-a" } },
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var document = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(cancellationToken));
        var reportJobId = document.RootElement.GetProperty("reportJobId").GetString()!;

        string? status = null;
        await Wait.UntilAsync(
            $"the job {reportJobId} to reach a terminal status",
            async token =>
            {
                status = await StatusAsync(connectionString, reportJobId, token);
                return status is "SUCCEEDED" or "FAILED" or "CANCELLED";
            },
            TerminalDeadline,
            TimeSpan.FromSeconds(1),
            cancellationToken);

        if (status == "QUEUED")
        {
            Assert.Fail(PrintingNotOnThisCommit);
        }

        Assert.Equal("SUCCEEDED", status);
    }

    private static async Task<string> ReadyUploadAsync(HttpClient api, CancellationToken cancellationToken)
    {
        using var created = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
            {
                Content = new StringContent("""{"profile":"header-items-v1","mode":"API_STREAM"}""", Encoding.UTF8, "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, "consumer-a" } },
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync(cancellationToken));
        var uploadId = createdDocument.RootElement.GetProperty("uploadId").GetString()!;

        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"header":{},"items":[{"id":1,"v":17}]}"""));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(CreateUploadRequestReader.JsonMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/content", UriKind.Relative))
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, "consumer-a");
        using (var sent = await api.SendAsync(request, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        using var confirmed = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/complete", UriKind.Relative))
            {
                Content = new StringContent("""{"sha256":null}""", Encoding.UTF8, "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, "consumer-a" } },
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);

        await Wait.UntilAsync(
            $"the upload {uploadId} to end ready",
            async token =>
            {
                using var read = new HttpRequestMessage(HttpMethod.Get, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}", UriKind.Relative));
                read.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, "consumer-a");
                using var response = await api.SendAsync(read, token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return false;
                }

                using var projection = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                return projection.RootElement.GetProperty("status").GetString() == "ready";
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return uploadId;
    }

    private static async Task<string?> StatusAsync(string connectionString, string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM report_service.report_jobs WHERE report_job_id = @ReportJobId",
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }
}
