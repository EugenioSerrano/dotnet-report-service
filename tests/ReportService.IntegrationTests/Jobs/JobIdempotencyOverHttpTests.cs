using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Telemetry;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// Idempotency where a consumer meets it: the product's own API, hosted from its real
/// registrations over a real PostgreSQL 18.6 database brought to the expected version by the
/// product's migrations, with a registry file and a storage root of the class's own. The
/// cases send the requests concurrently, because the decision is the acceptance transaction's
/// unique index and not a check between two round trips: sixteen identical requests give one
/// job and sixteen identical answers, and two different requests under one key give one
/// accepted body and a conflict for the other. A repeat is still answered after the first
/// job's upload was refused, and the instruments carry the repeat without ever carrying the
/// key or the job's identifier.
/// </summary>
public sealed class JobIdempotencyOverHttpTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Consumer = "consumer-a";
    private const string Other = "consumer-b";
    private const int Concurrent = 16;

    private readonly PostgresFixture _fixture;
    private readonly LogCapture _logs = new();
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "rsji",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    private string? _database;
    private string? _connectionString;
    private NpgsqlDataSource? _dataSource;
    private WebApplicationFactory<Program>? _api;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public JobIdempotencyOverHttpTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            Directory.CreateDirectory(_directory);
            _database = await _fixture.CreateDatabaseAsync(cancellationToken);
            _connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(_database))
            {
                Timeout = 5,
                CommandTimeout = PostgresJobStore.CommandTimeoutSeconds,
            }.ConnectionString;
            var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
            Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
            _dataSource = NpgsqlDataSource.Create(_connectionString);

            var registry = Path.Combine(_directory, "consumers.json");
            File.WriteAllText(
                registry,
                $$"""
                { "consumers": [
                    { "consumerId": "{{Consumer}}", "active": true },
                    { "consumerId": "{{Other}}", "active": true } ] }
                """);
            _api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
                builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory, "storage"));
                builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
                builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
                builder.ConfigureLogging(logging => logging.AddProvider(_logs));
            });
            using var client = _api.CreateClient();
            await Wait.UntilAsync(
                "the API answering /health",
                async token => (await client.GetAsync(new Uri("/health", UriKind.Relative), token)).StatusCode == HttpStatusCode.OK,
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMilliseconds(250),
                cancellationToken);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _api?.Dispose();
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        _logs.Dispose();
    }

    [Fact]
    public async Task SixteenIdenticalRequestsWithOneKeyGiveOneJobAndSixteenIdenticalAnswers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await ReadyUploadAsync(Consumer, cancellationToken);
        var body = BodyOf(uploadId, version: "1.0.0");

        // Released together: every request reaches the acceptance before any of them commits.
        var responses = await SendTogetherAsync(
            Enumerable.Range(0, Concurrent).Select(_ => new Submission(Consumer, body, "K1")), cancellationToken);

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        var identifiers = responses.Select(response => response.ReportJobId).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Single(identifiers);
        Assert.Single(responses.Select(response => response.Body).Distinct(StringComparer.Ordinal));

        Assert.Equal(1, await CountAsync("report_jobs", cancellationToken));
        Assert.Equal(1, await CountAsync("report_outputs", cancellationToken));
        Assert.Equal(1, await CountAsync("template_references", cancellationToken));
        Assert.Equal(1, await StartJobIntentsAsync(cancellationToken));
    }

    [Fact]
    public async Task TwoDifferentRequestsUnderOneKeyGiveOneAcceptedBodyAndAConflictForTheOther()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = await ReadyUploadAsync(Consumer, cancellationToken);
        var second = await ReadyUploadAsync(Consumer, cancellationToken);
        var request = new List<Submission>();
        request.AddRange(Enumerable.Range(0, Concurrent).Select(_ => new Submission(Consumer, BodyOf(first, "1.0.0"), "K1")));
        request.AddRange(Enumerable.Range(0, Concurrent).Select(_ => new Submission(Consumer, BodyOf(second, "1.0.0"), "K1")));

        var responses = await SendTogetherAsync(request, cancellationToken);

        Assert.Equal(Concurrent, responses.Count(response => response.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(Concurrent, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        Assert.All(
            responses.Where(response => response.StatusCode == HttpStatusCode.Conflict),
            response => Assert.Equal(ErrorCodes.IdempotencyConflict, response.Code));
        Assert.Single(
            responses.Where(response => response.StatusCode == HttpStatusCode.Accepted)
                .Select(response => response.ReportJobId).Distinct(StringComparer.Ordinal));
        Assert.Equal(1, await CountAsync("report_jobs", cancellationToken));
        Assert.Equal(1, await StartJobIntentsAsync(cancellationToken));
    }

    [Fact]
    public async Task ARepeatIsStillAnsweredAfterTheFirstJobsUploadWasRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await ReadyUploadAsync(Consumer, cancellationToken, verifying: true);
        var body = BodyOf(uploadId, version: "1.0.0");

        var accepted = Assert.Single(await SendTogetherAsync([new Submission(Consumer, body, "K1")], cancellationToken));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var uploads = new PostgresUploadStore(_dataSource!);
        Assert.True(await uploads.CommitRejectedAsync(
            uploadId,
            UploadRejectionCode.DataInvalid,
            UploadRejectionReason.Truncated,
            DateTimeOffset.UtcNow,
            cancellationToken));

        var repeat = Assert.Single(await SendTogetherAsync([new Submission(Consumer, body, "K1")], cancellationToken));

        Assert.Equal(HttpStatusCode.Accepted, repeat.StatusCode);
        Assert.Equal(accepted.ReportJobId, repeat.ReportJobId);
        Assert.Equal(1, await CountAsync("report_jobs", cancellationToken));
    }

    [Fact]
    public async Task TheRepeatIsCountedAndTheConflictIsCodedWithoutTheKeyOrTheJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await ReadyUploadAsync(Consumer, cancellationToken);
        var other = await ReadyUploadAsync(Consumer, cancellationToken);
        var body = BodyOf(uploadId, version: "1.0.0");
        const string Planted = "planted-key-4c1f";

        using var counter = new CounterListener();
        var first = Assert.Single(await SendTogetherAsync([new Submission(Consumer, body, Planted)], cancellationToken));
        var repeat = Assert.Single(await SendTogetherAsync([new Submission(Consumer, body, Planted)], cancellationToken));
        var conflict = Assert.Single(await SendTogetherAsync([new Submission(Consumer, BodyOf(other, "1.0.0"), Planted)], cancellationToken));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, repeat.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var measurements = counter.Measurements;
        Assert.Contains(measurements, measurement => measurement.Result == JobAcceptanceMetrics.AcceptedResult);
        Assert.Contains(measurements, measurement => measurement.Result == JobAcceptanceMetrics.RepeatedResult);
        Assert.Contains(
            measurements,
            measurement => measurement.Result == JobAcceptanceMetrics.RefusedResult
                && measurement.Code == ErrorCodes.IdempotencyConflict);
        Assert.DoesNotContain(measurements, measurement => measurement.Tags.Any(tag => tag.Contains(Planted, StringComparison.Ordinal)));
        Assert.DoesNotContain(_logs.Lines, line => line.Contains(Planted, StringComparison.Ordinal));

        // The job's identifier is on the repeat's request span, and the key is on no span at all.
        // The span ends when the request pipeline finishes, which can trail the answer the client
        // read, so the case waits for the tag rather than racing it.
        await Wait.UntilAsync(
            "the repeat's request span to carry the first job's identifier",
            _ => Task.FromResult(counter.SpanValues.Contains(first.ReportJobId)),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(100),
            cancellationToken);
        Assert.Contains(first.ReportJobId, counter.SpanValues);
        Assert.DoesNotContain(counter.SpanValues, value => value.Contains(Planted, StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<Answer>> SendTogetherAsync(
        IEnumerable<Submission> requests,
        CancellationToken cancellationToken)
    {
        using var api = _api!.CreateClient();
        var pending = requests.Select((request, index) => Task.Run(
            async () =>
            {
                using var message = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
                {
                    Content = new StringContent(request.Body, Encoding.UTF8, "application/json"),
                };
                message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, request.Consumer);
                if (request.Key is not null)
                {
                    message.Headers.TryAddWithoutValidation(IdempotencyKeyHeader.HeaderName, request.Key);
                }

                using var response = await api.SendAsync(message, cancellationToken);
                return await Answer.OfAsync(response, cancellationToken);
            },
            cancellationToken)).ToArray();

        return await Task.WhenAll(pending);
    }

    // One job's start request is one row of the intent table; the upload steps a case seeds its
    // data with write a start-upload-verification intent of their own, which is no part of what an
    // acceptance decides.
    private async Task<long> StartJobIntentsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM report_service.intents WHERE kind = 'start-job'",
            cancellationToken: cancellationToken));
    }

    private async Task<long> CountAsync(string table, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT count(*) FROM report_service.{table}",
            cancellationToken: cancellationToken));
    }

    private sealed record Submission(string Consumer, string Body, string? Key);

    private static string BodyOf(string uploadId, string? version)
    {
        var template = version is null
            ? """{"id":"pilot-grouped"}"""
            : $$"""{"id":"pilot-grouped","version":"{{version}}"}""";
        return $$"""{"template":{{template}},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":["pdf"]}""";
    }

    private async Task<string> ReadyUploadAsync(
        string consumerId,
        CancellationToken cancellationToken,
        bool verifying = false)
    {
        var now = DateTimeOffset.UtcNow;
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        var uploads = new PostgresUploadStore(_dataSource!);
        await uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                consumerId,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                ObjectKey.FromSegments("staging", uploadId, "content"),
                now,
                now.AddHours(1)),
            cancellationToken);
        Assert.True(await uploads.TryStartTransferAsync(uploadId, consumerId, UploadContentEncoding.None, now, cancellationToken));
        Assert.True(await uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, 900, 900, new string('a', 64), now, cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, consumerId, null, now, now, now.AddHours(1), cancellationToken));
        if (!verifying)
        {
            Assert.Equal(
                SnapshotCommitResult.Committed,
                await uploads.CommitReadyAsync(
                    new SnapshotRecord(
                        uploadId,
                        consumerId,
                        ObjectKey.FromSegments("snapshots", uploadId, "a"),
                        UploadContentEncoding.None,
                        DataProfile.HeaderItemsV1,
                        new string('a', 64),
                        900,
                        new string('a', 64),
                        900,
                        now),
                    now,
                    cancellationToken));
        }

        return uploadId;
    }

    // One answer, reduced to what a case asserts: its status, its body as sent, the members a
    // refusal or an acceptance carries, and nothing a case has to parse twice.
    private sealed record Answer(HttpStatusCode StatusCode, string Body, string? ReportJobId, string? Code)
    {
        public static async Task<Answer> OfAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            string? reportJobId = null;
            string? code = null;
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("reportJobId", out var identifier))
            {
                reportJobId = identifier.GetString();
            }

            if (document.RootElement.TryGetProperty("code", out var errorCode))
            {
                code = errorCode.GetString();
            }

            return new Answer(response.StatusCode, body, reportJobId, code);
        }
    }

    // Hears the acceptance counter and the request spans' tags for the length of one case. The
    // span a request runs under is the host's own, so the listener follows the API's framework
    // source beside the service's; a case asserts only values of its own, which no other suite's
    // span can carry.
    private sealed class CounterListener : IDisposable
    {
        private readonly MeterListener _meter = new();
        private readonly ActivityListener _activities;
        private readonly ConcurrentQueue<Measurement> _measurements = new();
        private readonly ConcurrentQueue<string> _spans = new();

        public CounterListener()
        {
            _meter.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == JobAcceptanceMetrics.InstrumentName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                string? result = null;
                string? code = null;
                var all = new List<string>();
                foreach (var tag in tags)
                {
                    all.Add(tag.Key + "=" + tag.Value);
                    if (tag.Key == "result")
                    {
                        result = tag.Value?.ToString();
                    }

                    if (tag.Key == "code")
                    {
                        code = tag.Value?.ToString();
                    }
                }

                _measurements.Enqueue(new Measurement(result, code, all));
            });
            _meter.Start();

            _activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name.StartsWith("ReportService", StringComparison.Ordinal)
                    || source.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.GetTagItem(CreateReportJobEndpoint.JobIdTag) is string jobId)
                    {
                        _spans.Enqueue(jobId);
                    }

                    foreach (var tag in activity.TagObjects)
                    {
                        _spans.Enqueue(tag.Key + "=" + tag.Value);
                    }
                },
            };
            ActivitySource.AddActivityListener(_activities);
        }

        public IReadOnlyList<Measurement> Measurements => [.. _measurements];

        public IReadOnlyList<string> SpanValues => [.. _spans];

        public void Dispose()
        {
            _meter.Dispose();
            _activities.Dispose();
        }
    }

    private sealed record Measurement(string? Result, string? Code, IReadOnlyList<string> Tags);
}
