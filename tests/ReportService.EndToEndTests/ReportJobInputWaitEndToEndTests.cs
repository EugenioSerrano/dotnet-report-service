using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Uploads;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// A job created while its upload is still being verified, on the running composition: the
/// acceptance answers <c>202 queued</c> while the worker is stopped and the store shows the
/// job with no stage; once the worker runs the job waits for the upload, and the upload's own
/// outcome ends it — with the upload's rejection code when the body is refused. Every step is
/// the product's own operation, and the composition's processes are the ones a deployment runs.
/// </summary>
public sealed class ReportJobInputWaitEndToEndTests : IDisposable
{
    private const string Registry = """
        { "consumers": [
            { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true } ] }
        """;

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan OutcomeDeadline = TimeSpan.FromMinutes(3);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public ReportJobInputWaitEndToEndTests() => Directory.CreateDirectory(_directory);

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task AJobAcceptedWhileItsUploadVerifiesIsQueuedWithoutAStageAndWaitsOnceTheWorkerRuns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (app, api) = await StartAsync(cancellationToken);
        await using var composition = app;
        var connectionString = await ConnectionStringAsync(app, cancellationToken);
        var commands = app.Services.GetRequiredService<ResourceCommandService>();

        // The upload is confirmed while the orchestration worker is stopped, so nothing starts
        // its verification and nothing starts a job.
        await commands.ExecuteCommandAsync(Resource(app, "orchestration-worker"), KnownResourceCommands.StopCommand, cancellationToken);
        var uploadId = await CreateUploadAsync(api, cancellationToken);
        await StreamAsync(api, uploadId, """{"header":{},"items":[{"id":1,"v":17}]}""", cancellationToken);
        await ConfirmAsync(api, uploadId, cancellationToken);
        Assert.Equal("verifying", await UploadStatusAsync(api, uploadId, cancellationToken));

        var reportJobId = await AcceptJobAsync(api, uploadId, cancellationToken);
        var before = await JobAsync(connectionString, reportJobId, cancellationToken);
        Assert.NotNull(before);
        Assert.Equal("QUEUED", before!.Status);
        Assert.Null(before.Stage);
        Assert.Equal(0, await ReferencesAsync(connectionString, reportJobId, cancellationToken));

        // The worker runs: the job records that it waits and the verification settles the upload.
        await commands.ExecuteCommandAsync(Resource(app, "orchestration-worker"), KnownResourceCommands.StartCommand, cancellationToken);
        await Wait.UntilAsync(
            $"the job {reportJobId} to leave the wait",
            async token =>
            {
                var job = await JobAsync(connectionString, reportJobId, token);
                return job is { Status: "QUEUED", Stage: null };
            },
            OutcomeDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

        Assert.Equal("ready", await UploadStatusAsync(api, uploadId, cancellationToken));
        Assert.Equal(1, await ReferencesAsync(connectionString, reportJobId, cancellationToken));
    }

    [Fact]
    public async Task ARejectedBodyEndsTheJobWithTheConsumerVisibleCode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (app, api) = await StartAsync(cancellationToken);
        await using var composition = app;
        var connectionString = await ConnectionStringAsync(app, cancellationToken);

        // A body the content check refuses: the upload ends rejected and the job carries its code.
        var uploadId = await CreateUploadAsync(api, cancellationToken);
        await StreamAsync(api, uploadId, """{"header":{},"items":[{"id":"not-a-number"}]}""", cancellationToken);
        await ConfirmAsync(api, uploadId, cancellationToken);
        var reportJobId = await AcceptJobAsync(api, uploadId, cancellationToken);

        await Wait.UntilAsync(
            $"the job {reportJobId} to end",
            async token => (await JobAsync(connectionString, reportJobId, token))?.Status is "FAILED",
            OutcomeDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

        var job = await JobAsync(connectionString, reportJobId, cancellationToken);
        Assert.Equal("FAILED", job!.Status);
        Assert.Equal("DATA_INVALID", job.ErrorCode);
        Assert.Equal("input-rejected", job.ErrorReason);
    }

    private static IResource Resource(DistributedApplication app, string name) =>
        app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources
            .Single(resource => string.Equals(resource.Name, name, StringComparison.Ordinal));

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

    private static async Task<string> ConnectionStringAsync(DistributedApplication app, CancellationToken cancellationToken) =>
        await app.GetConnectionStringAsync("reportservice", cancellationToken)
            ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");

    private static async Task<string> CreateUploadAsync(HttpClient api, CancellationToken cancellationToken)
    {
        using var created = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
            {
                Content = new StringContent("""{"profile":"header-items-v1","mode":"API_STREAM"}""", Encoding.UTF8, "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, "consumer-a" } },
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("uploadId").GetString()!;
    }

    private static async Task StreamAsync(HttpClient api, string uploadId, string body, CancellationToken cancellationToken)
    {
        using var content = new StringContent(body, Encoding.UTF8, CreateUploadRequestReader.JsonMediaType);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/content", UriKind.Relative))
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, "consumer-a");
        using var sent = await api.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
    }

    private static async Task ConfirmAsync(HttpClient api, string uploadId, CancellationToken cancellationToken)
    {
        using var confirmed = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/complete", UriKind.Relative))
            {
                Content = new StringContent("{}", Encoding.UTF8, CreateUploadRequestReader.JsonMediaType),
                Headers = { { ConsumerIdentity.HeaderName, "consumer-a" } },
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
    }

    private static async Task<string> AcceptJobAsync(HttpClient api, string uploadId, CancellationToken cancellationToken)
    {
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
        var body = await accepted.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("queued", document.RootElement.GetProperty("status").GetString());
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

    private static async Task<string?> UploadStatusAsync(HttpClient api, string uploadId, CancellationToken cancellationToken)
    {
        string? status = null;
        await Wait.UntilAsync(
            $"the upload {uploadId} to be readable",
            async token =>
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}", UriKind.Relative));
                request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, "consumer-a");
                using var response = await api.SendAsync(request, token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return false;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                status = document.RootElement.GetProperty("status").GetString();
                return true;
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return status;
    }

    private static async Task<JobRow?> JobAsync(string connectionString, string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(
            """
            SELECT report_job_id AS "ReportJobId", status AS "Status", stage AS "Stage",
                   error_code AS "ErrorCode", error_reason AS "ErrorReason"
              FROM report_service.report_jobs
             WHERE report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private static async Task<long> ReferencesAsync(string connectionString, string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT count(*)
              FROM report_service.execution_references reference
              JOIN report_service.intents intent ON intent.intent_id = reference.intent_id
             WHERE intent.kind = 'start-job' AND intent.subject_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    /// <summary>One job row, as the composition's database holds it.</summary>
    private sealed class JobRow
    {
        public string ReportJobId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? Stage { get; set; }

        public string? ErrorCode { get; set; }

        public string? ErrorReason { get; set; }
    }
}
