using System.Globalization;
using System.Net;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Npgsql;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// The cancel request surface and the durable cancel intent, over the real API process and the
/// composition's PostgreSQL metadata database (no Temporal — the wind-down and terminal
/// CANCELLED are US-010.BOLT-002). The oracles are US-010 AC-1 / AC-6 and ADR-012 §3.10(1),
/// never the implementation (G06): a cancel of a non-terminal job is accepted, commits a durable
/// <c>cancel-job</c> row in <c>report_service.intents</c> and a real read reports
/// <c>CANCELLING</c>; a repeat commits no second intent and does not regress; a cancel of a
/// terminal job records no intent and leaves it unchanged; and a transport event (a client that
/// never completes its cancel request) never cancels.
/// <para>
/// The jobs are seeded straight into the composition's stores — a verifying upload and an
/// accepted job for the non-terminal cases, the same then failed for the terminal case — so the
/// cases do not depend on the render path; the API is exercised only for the cancel surface, and
/// the assertions read the real metadata database the API wrote to.
/// </para>
/// </summary>
public sealed class CancelRequestDurableIntentTests
{
    private const string Consumer = "consumer-a";

    private const string Template = "pilot-grouped";

    private const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";

    private const string CancelJobKind = "cancel-job";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task ACancelOfANonTerminalJobCommitsADurableCancelIntentAndReportsCancelling()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await StartAsync(cancellationToken);
        await using var dataSource = await DataSourceAsync(composition, cancellationToken);
        using var api = composition.CreateHttpClient("api");

        var reportJobId = await SeedNonTerminalJobAsync(dataSource, cancellationToken);

        using var response = await CancelAsync(api, reportJobId, cancellationToken);
        var answer = await AnswerAsync(response, cancellationToken);

        // AC-1: the request is accepted, the body returns the current state CANCELLING.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("cancelling", answer.GetProperty("status").GetString());

        // The cancel intent is a real committed row (exactly one), and a real read reports CANCELLING.
        Assert.Equal(1, await CancelIntentCountAsync(dataSource, reportJobId, cancellationToken));
        Assert.Equal("CANCELLING", await JobStatusAsync(dataSource, reportJobId, cancellationToken));
    }

    [Fact]
    public async Task ASecondCancelCommitsNoSecondIntentAndDoesNotRegress()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await StartAsync(cancellationToken);
        await using var dataSource = await DataSourceAsync(composition, cancellationToken);
        using var api = composition.CreateHttpClient("api");

        var reportJobId = await SeedNonTerminalJobAsync(dataSource, cancellationToken);

        using (var first = await CancelAsync(api, reportJobId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        }

        using var repeat = await CancelAsync(api, reportJobId, cancellationToken);

        // AC-1 idempotent repeat: still accepted, no regress, and a single intent row only.
        Assert.Equal(HttpStatusCode.Accepted, repeat.StatusCode);
        Assert.Equal("cancelling", (await AnswerAsync(repeat, cancellationToken)).GetProperty("status").GetString());
        Assert.Equal(1, await CancelIntentCountAsync(dataSource, reportJobId, cancellationToken));
        Assert.Equal("CANCELLING", await JobStatusAsync(dataSource, reportJobId, cancellationToken));
    }

    [Fact]
    public async Task ACancelOfATerminalJobRecordsNoIntentAndLeavesItUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await StartAsync(cancellationToken);
        await using var dataSource = await DataSourceAsync(composition, cancellationToken);
        using var api = composition.CreateHttpClient("api");

        var store = new PostgresJobStore(dataSource);
        var reportJobId = await SeedNonTerminalJobAsync(dataSource, cancellationToken);
        Assert.Equal(
            JobFailureOutcome.Applied,
            await store.FailJobAsync(reportJobId, "QUOTA_EXCEEDED", "block", DateTimeOffset.UtcNow, cancellationToken));

        using var response = await CancelAsync(api, reportJobId, cancellationToken);

        // AC-6: a terminal job's cancel is an idempotent no-op — 200, the state is unchanged, no intent.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("failed", (await AnswerAsync(response, cancellationToken)).GetProperty("status").GetString());
        Assert.Equal("FAILED", await JobStatusAsync(dataSource, reportJobId, cancellationToken));
        Assert.Equal(0, await CancelIntentCountAsync(dataSource, reportJobId, cancellationToken));
    }

    [Fact]
    public async Task ATransportEventAgainstTheRealApiNeverCancels()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await StartAsync(cancellationToken);
        await using var dataSource = await DataSourceAsync(composition, cancellationToken);
        using var api = composition.CreateHttpClient("api");

        var reportJobId = await SeedNonTerminalJobAsync(dataSource, cancellationToken);
        var before = await JobStatusAsync(dataSource, reportJobId, cancellationToken);

        // A transport event: the client gives up before its cancel request is delivered and
        // processed. AC-6/R7 — only an explicit, completed cancel request cancels — so the job
        // must stay non-terminal and no cancel intent must appear.
        using (var aborted = new CancellationTokenSource())
        {
            await aborted.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                api.SendAsync(CancelRequest(reportJobId), aborted.Token));
        }

        // Settle, then confirm nothing was cancelled by the transport event.
        await Task.Delay(SettleWindow, cancellationToken);
        Assert.Equal(before, await JobStatusAsync(dataSource, reportJobId, cancellationToken));
        Assert.NotEqual("CANCELLING", await JobStatusAsync(dataSource, reportJobId, cancellationToken));
        Assert.Equal(0, await CancelIntentCountAsync(dataSource, reportJobId, cancellationToken));
    }

    // Starts the composition with a registry carrying the active fixture consumer and waits for health.
    private static async Task<DistributedApplication> StartAsync(CancellationToken cancellationToken)
    {
        var registry = Path.Combine(
            Path.GetTempPath(),
            "report-service-e2e-cancel",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8],
            "consumers.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        await File.WriteAllTextAsync(
            registry,
            """{ "consumers": [ { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true } ] }""",
            cancellationToken);

        var builder = await TestComposition.CreateAsync(cancellationToken, $"--Consumers:File={registry}");
        var app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using var api = app.CreateHttpClient("api");
        await Wait.UntilAsync(
            "the API answering /health",
            async token =>
            {
                using var response = await api.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            HealthyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return app;
    }

    private static async Task<NpgsqlDataSource> DataSourceAsync(DistributedApplication composition, CancellationToken cancellationToken)
    {
        var connectionString = await composition.GetConnectionStringAsync("reportservice", cancellationToken)
            ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");
        return NpgsqlDataSource.Create(connectionString);
    }

    // A non-terminal (QUEUED) job seeded directly into the composition's stores over a verifying
    // upload — no render path is needed to prove the cancel surface and the durable intent.
    private static async Task<string> SeedNonTerminalJobAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var uploads = new PostgresUploadStore(dataSource);
        var jobs = new PostgresJobStore(dataSource);
        var now = DateTimeOffset.UtcNow;

        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        await uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                Consumer,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                ObjectKey.FromSegments("staging", uploadId, "content"),
                now,
                now.AddHours(2)),
            cancellationToken);
        Assert.True(await uploads.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.None, now, cancellationToken));
        Assert.True(await uploads.RecordTransferAsync(uploadId, UploadContentOutcome.Complete, 900, 800, Hash, now, cancellationToken));
        // Confirmed but not committed-ready: the upload stays verifying with a far-future deadline,
        // so the job reliably stays non-terminal while the cancel is requested.
        Assert.True(await uploads.ConfirmAsync(uploadId, Consumer, null, now, now, now.AddHours(2), cancellationToken));

        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            [new RequestedOutput(JobIdentifier.CreateOutput(), OutputFormat.Pdf)],
            Consumer,
            uploadId,
            Template,
            TemplateReferenceForm.Exact,
            "1.0.0",
            AcceptanceIdentity.For(uploadId));
        Assert.IsType<JobAcceptanceResult.Accepted>(await jobs.AcceptAsync(acceptance, now, cancellationToken));
        return acceptance.ReportJobId;
    }

    private static Task<HttpResponseMessage> CancelAsync(HttpClient api, string reportJobId, CancellationToken cancellationToken) =>
        api.SendAsync(CancelRequest(reportJobId), cancellationToken);

    private static HttpRequestMessage CancelRequest(string reportJobId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}/cancel", UriKind.Relative));
        message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        return message;
    }

    private static async Task<JsonElement> AnswerAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<int> CancelIntentCountAsync(NpgsqlDataSource dataSource, string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM report_service.intents WHERE kind = @kind AND subject_id = @id";
        command.Parameters.Add(new NpgsqlParameter("kind", CancelJobKind));
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<string> JobStatusAsync(NpgsqlDataSource dataSource, string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM report_service.report_jobs WHERE report_job_id = @id";
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
