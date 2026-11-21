using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Dapper;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Uploads;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// A repeated job request on the running composition: an upload of a consumer the temporary
/// registry holds is streamed and confirmed, a job is accepted with an idempotency key, and
/// the same request sent again is answered the same job while the composition's own database
/// holds exactly one. The same key with another upload is refused with a conflict and leaves
/// the first job as it was, and another consumer's use of the same key is its own job. The
/// cases read the database because the status read is a later operation, and they print
/// nothing: every case ends with a job accepted.
/// </summary>
public sealed class ReportJobIdempotencyEndToEndTests : IDisposable
{
    private const string Registry = """
        { "consumers": [
            { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true },
            { "consumerId": "consumer-b", "displayName": "Consumer B", "active": true },
            { "consumerId": "consumer-c", "displayName": "Consumer C", "active": false } ] }
        """;

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(3);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public ReportJobIdempotencyEndToEndTests() => Directory.CreateDirectory(_directory);

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ARepeatAnswersTheFirstJobAConflictLeavesItAndAnotherConsumersKeyIsItsOwn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (app, api) = await StartAsync(cancellationToken);
        await using var composition = app;
        var connectionString = await app.GetConnectionStringAsync("reportservice", cancellationToken)
            ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");

        var uploadA = await StreamAndConfirmAsync(api, ConsumerA(), cancellationToken);
        Assert.Equal("ready", await WaitForUploadStatusAsync(api, ConsumerA(), uploadA, cancellationToken));

        // The first request under the key is accepted, and its job is durable.
        using var accepted = await api.SendAsync(JobRequest(ConsumerA(), uploadA, "K1"), cancellationToken);
        var acceptedBody = await accepted.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var reportJobId = ReportJobIdOf(acceptedBody);
        var firstRow = await JobAsync(connectionString, reportJobId, cancellationToken);
        Assert.NotNull(firstRow);

        // The same request with the same key is answered the same job, byte for byte, and the
        // database still holds one job for this consumer.
        using var repeat = await api.SendAsync(JobRequest(ConsumerA(), uploadA, "K1"), cancellationToken);
        var repeatBody = await repeat.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, repeat.StatusCode);
        Assert.Equal(reportJobId, ReportJobIdOf(repeatBody));
        Assert.Equal(acceptedBody, repeatBody);
        Assert.Equal(accepted.Headers.Location!.OriginalString, repeat.Headers.Location!.OriginalString);
        Assert.Equal(1, await JobsOfAsync(connectionString, ConsumerA(), cancellationToken));

        // The same key with another upload is refused, and the first job is untouched.
        var uploadOther = await StreamAndConfirmAsync(api, ConsumerA(), cancellationToken);
        Assert.Equal("ready", await WaitForUploadStatusAsync(api, ConsumerA(), uploadOther, cancellationToken));
        using var conflict = await api.SendAsync(JobRequest(ConsumerA(), uploadOther, "K1"), cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(ErrorCodes.IdempotencyConflict, await CodeAsync(conflict, cancellationToken));
        var afterConflict = await JobAsync(connectionString, reportJobId, cancellationToken);
        Assert.Equal(firstRow!.UploadId, afterConflict!.UploadId);
        Assert.Equal(firstRow.Status, afterConflict.Status);
        Assert.Equal(1, await JobsOfAsync(connectionString, ConsumerA(), cancellationToken));

        // Another consumer's use of the same key is a job of its own.
        var uploadB = await StreamAndConfirmAsync(api, ConsumerB(), cancellationToken);
        Assert.Equal("ready", await WaitForUploadStatusAsync(api, ConsumerB(), uploadB, cancellationToken));
        using var otherConsumer = await api.SendAsync(JobRequest(ConsumerB(), uploadB, "K1"), cancellationToken);
        var otherBody = await otherConsumer.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, otherConsumer.StatusCode);
        Assert.NotEqual(reportJobId, ReportJobIdOf(otherBody));
        Assert.Equal(1, await JobsOfAsync(connectionString, ConsumerB(), cancellationToken));

        // And its own repeat answers its own job.
        using var otherRepeat = await api.SendAsync(JobRequest(ConsumerB(), uploadB, "K1"), cancellationToken);
        Assert.Equal(ReportJobIdOf(otherBody), ReportJobIdOf(await otherRepeat.Content.ReadAsStringAsync(cancellationToken)));

        // The registration is recorded as a transcript of this run, the composition's own rows
        // being the evidence: the first job, its one output and one reference, and two jobs.
        Assert.Equal(2, await CountAsync(connectionString, "report_jobs", cancellationToken));
        Assert.Equal(1, await CountAsync(connectionString, "report_outputs", cancellationToken));
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

    private static string ConsumerB() => "consumer-b";

    private static string ReportJobIdOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

    private static async Task<string> StreamAndConfirmAsync(HttpClient api, string consumer, CancellationToken cancellationToken)
    {
        var uploadId = await CreateUploadAsync(api, consumer, cancellationToken);
        var body = Encoding.UTF8.GetBytes("""{"header":{},"items":[{"id":1,"v":17}]}""");
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(CreateUploadRequestReader.JsonMediaType);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/content", UriKind.Relative))
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
                Content = new StringContent("{}", Encoding.UTF8, CreateUploadRequestReader.JsonMediaType),
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
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}", UriKind.Relative));
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

    private static HttpRequestMessage JobRequest(string consumer, string uploadId, string key)
    {
        var body = $$"""{"template":{"id":"pilot-grouped","version":"1.0.0"},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":["pdf"]}""";
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumer);
        request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader.HeaderName, key);
        return request;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("code").GetString();
    }

    private static async Task<long> JobsOfAsync(string connectionString, string consumerId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM report_service.report_jobs WHERE consumer_id = @ConsumerId",
            new { ConsumerId = consumerId },
            cancellationToken: cancellationToken));
    }

    private static async Task<long> CountAsync(string connectionString, string table, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT count(*) FROM report_service.{table}",
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

    /// <summary>One job row, as the composition's database holds it.</summary>
    private sealed class JobRow
    {
        public string ReportJobId { get; set; } = string.Empty;

        public string ConsumerId { get; set; } = string.Empty;

        public string UploadId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;
    }
}
