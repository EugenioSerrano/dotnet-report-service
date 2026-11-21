extern alias orchestration;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Uploads;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.Testing;
using ReportService.Uploads;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Client;
using Temporalio.Common;
using OrchestrationWorkerProgram = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;
using UploadExpiryOptions = orchestration::ReportService.Workers.Orchestration.Uploads.UploadExpiryOptions;
using UploadVerificationActivities = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationActivities;
using UploadVerificationOptions = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationOptions;
using UploadVerificationWorkflow = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationWorkflow;
using VerificationOptions = orchestration::ReportService.Workers.Orchestration.Uploads.VerificationOptions;
using VerificationResult = orchestration::ReportService.Workers.Orchestration.Uploads.VerificationResult;
using VerifyUploadResult = orchestration::ReportService.Workers.Orchestration.Uploads.VerifyUploadResult;

namespace ReportService.IntegrationTests.Uploads;

/// <summary>
/// A confirmed upload whose verification has not finished when its own verification
/// deadline passes: the run supplies the instants the case reads and keeps the
/// verification from running until the deadline has passed. The API and the worker
/// are the product's own registrations hosted in this process on one clock the class
/// moves, the confirmation records the deadline and the durable request in the
/// product's own transaction, and no worker exists while the two reads are made, so
/// the upload reads verifying before its deadline, expired at it, and the
/// verification that is then allowed to run commits nothing. The per-step record
/// places every expected result beside the observed value and is written into the
/// run's evidence folder when the process was given one.
/// </summary>
public sealed class UploadVerificationOutlastsItsDeadlineTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Consumer = "dev-consumer";

    private const string Uploading = "uploading";
    private const string Verifying = "verifying";
    private const string Expired = "expired";
    private const string StoredVerifying = "VERIFYING";
    private const string StoredExpired = "EXPIRED";
    private const string Pending = "PENDING";
    private const string Dispatched = "DISPATCHED";

    /// <summary>How long after its confirmation a confirmed upload may still fix a snapshot.</summary>
    private static readonly TimeSpan VerificationDeadline = TimeSpan.FromSeconds(30);

    /// <summary>How long a new upload session accepts data.</summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromSeconds(20);

    /// <summary>How long after the session opens the upload is confirmed, inside its last half of the verification deadline.</summary>
    private static readonly TimeSpan ConfirmationDelay = TimeSpan.FromSeconds(10);

    /// <summary>How long one verification attempt may run, under the deadline above.</summary>
    private static readonly TimeSpan StartToClose = TimeSpan.FromSeconds(10);

    /// <summary>How often the worker looks for uploads that ran out of time; nothing sweeps inside this run.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>The instant the run's clock starts at: fixed, so the record is readable and the run is independent of the machine's date.</summary>
    private static readonly DateTimeOffset ClockStart = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan DispatchDeadline = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan AttemptDeadline = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-upload-deadline-case",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private readonly ManualTimeProvider _clock = new(ClockStart);

    private readonly StringBuilder _record = new();

    private EngineStart.Engine? _engine;

    private NpgsqlDataSource? _dataSource;

    private string? _connectionString;

    private WebApplicationFactory<Program>? _api;

    private WebApplicationFactory<OrchestrationWorkerProgram>? _worker;

    private HttpClient? _apiClient;

    private HttpClient? _workerClient;

    private string? _uploadId;

    private DateTimeOffset _confirmedAt;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public UploadVerificationOutlastsItsDeadlineTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        try
        {
            await OpenAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task OpenAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        var database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(database))
        {
            Timeout = 5,
            CommandTimeout = PostgresUploadStore.CommandTimeoutSeconds,
        }.ConnectionString;

        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(_connectionString);

        _engine = await EngineStart.StartAsync(_engineImage, "temporal-upload-deadline-case", cancellationToken);

        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(registry, """{ "consumers": [ { "consumerId": "dev-consumer", "active": true } ] }""");

        // The API's own registration, on the run's clock, with a session short enough that
        // the confirmation falls inside the last half of the verification deadline.
        _api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
            builder.UseSetting($"{ObjectStorageSettings.SectionPath}:{nameof(ObjectStorageSettings.Filesystem)}:{nameof(FilesystemStorageOptions.Root)}", StorageRoot());
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.UseSetting(
                $"{UploadOptions.SectionPath}:{UploadOptions.SessionLifetimeSetting}",
                Duration(SessionLifetime));
            builder.UseSetting(
                $"{UploadOptions.SectionPath}:{UploadOptions.VerificationDeadlineSetting}",
                Duration(VerificationDeadline));
            builder.ConfigureTestServices(services => services
                .RemoveAll<TimeProvider>()
                .AddSingleton<TimeProvider>(_clock));
        });

        // The worker's own registration, on the same clock, built here and started only when
        // the held-back verification is let run: the host's first dispatcher poll happens at
        // its start, and that is the moment the pending request would be claimed.
        _worker = new WebApplicationFactory<OrchestrationWorkerProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_engine.HostPort.ToString(CultureInfo.InvariantCulture)}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting($"{ObjectStorageSettings.SectionPath}:{nameof(ObjectStorageSettings.Provider)}", FilesystemObjectStore.ProviderName);
            builder.UseSetting($"{ObjectStorageSettings.SectionPath}:{nameof(ObjectStorageSettings.Filesystem)}:{nameof(FilesystemStorageOptions.Root)}", StorageRoot());
            builder.UseSetting($"{ObjectStorageSettings.SectionPath}:{nameof(ObjectStorageSettings.Filesystem)}:{nameof(FilesystemStorageOptions.CreateIfMissing)}", "true");
            builder.UseSetting("Retention:Outputs", "01:00:00");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.UseSetting(
                $"{UploadVerificationOptions.SectionPath}:{UploadVerificationOptions.StartToCloseSetting}",
                Duration(StartToClose));
            builder.UseSetting(
                $"{VerificationOptions.SectionPath}:{VerificationOptions.VerificationDeadlineSetting}",
                Duration(VerificationDeadline));
            builder.UseSetting(
                $"{UploadExpiryOptions.SectionPath}:{UploadExpiryOptions.SweepIntervalSetting}",
                Duration(SweepInterval));
            builder.ConfigureTestServices(services => services
                .RemoveAll<TimeProvider>()
                .AddSingleton<TimeProvider>(_clock));
        });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _workerClient?.Dispose();
        _apiClient?.Dispose();

        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }

        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_engine is not null)
        {
            await _engine.Container.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    [Fact]
    public async Task AVerificationThatOutlastsItsDeadlineNeverBecomesASnapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            _apiClient = _api!.CreateClient();
            await WaitForHealthyAsync(_apiClient, "the API's readiness", cancellationToken);

            var t = await ConfirmAsync(cancellationToken);
            await ReadBeforeTheDeadlineAsync(t, cancellationToken);
            await ReadAtTheDeadlineAsync(t, cancellationToken);
            await AssertTheAbsenceBeforeTheAttemptAsync(cancellationToken);
            await LetTheHeldBackVerificationRunAsync(t, cancellationToken);
            await AssertTheStorageRootAsync(cancellationToken);
            await RepeatTheConfirmationsAsync(cancellationToken);
        }
        finally
        {
            WriteRecord();
        }
    }

    // Step 1: a real create, stream and confirm over the API, then the record the
    // confirmation wrote, read from the store, never written by this class.
    private async Task<DateTimeOffset> ConfirmAsync(CancellationToken cancellationToken)
    {
        var created = await CreateUploadAsync(cancellationToken);
        _uploadId = created.UploadId;

        using (var content = new ByteArrayContent(ValidBody()))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(CreateUploadRequestReader.JsonMediaType);
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                new Uri($"{UploadEndpoints.RoutePrefix}/{created.UploadId}/content", UriKind.Relative))
            {
                Content = content,
            };
            request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
            using var sent = await _apiClient!.SendAsync(request, cancellationToken);
            using var body = JsonDocument.Parse(await sent.Content.ReadAsStringAsync(cancellationToken));
            var uploaded = sent.StatusCode == HttpStatusCode.OK
                ? WireTextOf(body.RootElement, "status")
                : $"(no status; {(int)sent.StatusCode})";
            Check(
                "step 1 — the complete body is accepted as the upload's content",
                "200 with status \"uploading\"",
                $"{(int)sent.StatusCode} with status \"{uploaded}\"",
                sent.StatusCode == HttpStatusCode.OK && string.Equals(uploaded, Uploading, StringComparison.Ordinal));

            _clock.Advance(ConfirmationDelay);
            var confirmation = _clock.GetUtcNow();

            Check(
                "arrangement — the confirmation instant falls inside the last half of the verification deadline before expiresAt",
                $"expiresAt - d/2 ({UploadWireValues.FormatTimestamp(created.ExpiresAt - (VerificationDeadline / 2))}) < t ({UploadWireValues.FormatTimestamp(confirmation)}) < expiresAt ({UploadWireValues.FormatTimestamp(created.ExpiresAt)})",
                $"{UploadWireValues.FormatTimestamp(created.ExpiresAt - (VerificationDeadline / 2))} < {UploadWireValues.FormatTimestamp(confirmation)} < {UploadWireValues.FormatTimestamp(created.ExpiresAt)}",
                created.ExpiresAt - (VerificationDeadline / 2) < confirmation && confirmation < created.ExpiresAt);

            var confirmed = await CompleteAsync(declaration: null, cancellationToken);
            var record = await UploadAsync(cancellationToken);
            var intent = await IntentAsync(cancellationToken);

            Check(
                "step 1 — the confirmation answers 202 verifying with completedAt = t, and the record carries verification_deadline = t + d",
                $"202 with status \"{Verifying}\" and completedAt {UploadWireValues.FormatTimestamp(confirmation)}; the record reads {StoredVerifying} with completed_at {UploadWireValues.FormatTimestamp(confirmation)} and verification_deadline {UploadWireValues.FormatTimestamp(confirmation + VerificationDeadline)}",
                $"{(int)confirmed.StatusCode} with status \"{confirmed.Status}\" and completedAt {confirmed.CompletedAt ?? "(null)"}; the record reads {record?.Status ?? "(no record)"} with completed_at {Wire(record?.CompletedAt)} and verification_deadline {Wire(record?.VerificationDeadline)}",
                confirmed.StatusCode == HttpStatusCode.Accepted
                && string.Equals(confirmed.Status, Verifying, StringComparison.Ordinal)
                && string.Equals(confirmed.CompletedAt, UploadWireValues.FormatTimestamp(confirmation), StringComparison.Ordinal)
                && record is { Status: StoredVerifying }
                && SameInstant(record.CompletedAt, confirmation)
                && record.VerificationDeadline is { } deadline
                && SameInstant(deadline, confirmation + VerificationDeadline));

            Check(
                "arrangement — the confirmation left one pending start-upload-verification request at t, and no one has served it",
                "one request with status PENDING and next_attempt_at = t",
                intent is null ? "(no request)" : $"one request with status {intent.Status} and next_attempt_at {Wire(intent.NextAttemptAt)}",
                intent is { Status: Pending } && SameInstant(intent.NextAttemptAt, confirmation));

            Check(
                "arrangement — the upload's created and expires instants differ by the session lifetime",
                $"expiresAt - createdAt = {Duration(SessionLifetime)}",
                $"{UploadWireValues.FormatTimestamp(created.ExpiresAt)} - {UploadWireValues.FormatTimestamp(created.CreatedAt)} = {created.ExpiresAt - created.CreatedAt}",
                created.ExpiresAt - created.CreatedAt == SessionLifetime);

            _confirmedAt = confirmation;
            return confirmation;
        }
    }

    // Step 2: the status read clearly before the verification deadline, while the
    // session has already ended.
    private async Task ReadBeforeTheDeadlineAsync(DateTimeOffset confirmedAt, CancellationToken cancellationToken)
    {
        _clock.Advance(VerificationDeadline / 2);
        var now = _clock.GetUtcNow();
        var read = await ReadStatusAsync(cancellationToken);
        var record = await UploadAsync(cancellationToken);
        var intent = await IntentAsync(cancellationToken);
        var references = await ExecutionReferenceCountAsync(cancellationToken);

        Check(
            "step 2 — the status read at t + d/2 answers verifying, although expiresAt is already behind it",
            $"200 with status \"{Verifying}\"; the record still {StoredVerifying}; expiresAt ({UploadWireValues.FormatTimestamp(confirmedAt + SessionLifetime - ConfirmationDelay)}) behind the read ({UploadWireValues.FormatTimestamp(now)})",
            $"{(int)read.StatusCode} with status \"{read.Status}\"; the record reads {record?.Status ?? "(no record)"}; expiresAt {Wire(record?.ExpiresAt)} beside the read at {UploadWireValues.FormatTimestamp(now)}",
            read.StatusCode == HttpStatusCode.OK
            && string.Equals(read.Status, Verifying, StringComparison.Ordinal)
            && record is { Status: StoredVerifying }
            && new DateTimeOffset(DateTime.SpecifyKind(record.ExpiresAt, DateTimeKind.Utc)) < now);

        Check(
            "arrangement — nothing has served the verification at t + d/2",
            $"the request is still {Pending} and no execution reference exists",
            $"the request is {intent?.Status ?? "(none)"} and {references} execution reference(s) exist",
            intent is { Status: Pending } && references == 0);
    }

    // Step 3: the status read at the deadline itself.
    private async Task ReadAtTheDeadlineAsync(DateTimeOffset confirmedAt, CancellationToken cancellationToken)
    {
        _clock.Advance(VerificationDeadline / 2);
        var now = _clock.GetUtcNow();
        var read = await ReadStatusAsync(cancellationToken);
        var record = await UploadAsync(cancellationToken);

        Check(
            "step 3 — at t + d, the deadline itself, the status read answers expired and the stored record is EXPIRED",
            $"200 with status \"{Expired}\"; the record {StoredExpired}; the read instant is t + d = {UploadWireValues.FormatTimestamp(confirmedAt + VerificationDeadline)}",
            $"{(int)read.StatusCode} with status \"{read.Status}\"; the record reads {record?.Status ?? "(no record)"}; the read instant is {UploadWireValues.FormatTimestamp(now)}",
            read.StatusCode == HttpStatusCode.OK
            && string.Equals(read.Status, Expired, StringComparison.Ordinal)
            && record is { Status: StoredExpired }
            && now == confirmedAt + VerificationDeadline);
    }

    // Step 4's "before" half: nothing is committed while no worker exists.
    private async Task AssertTheAbsenceBeforeTheAttemptAsync(CancellationToken cancellationToken)
    {
        var snapshots = await SnapshotRecordCountAsync(cancellationToken);
        var objects = await SnapshotObjectsAsync(cancellationToken);
        var intent = await IntentAsync(cancellationToken);
        var references = await ExecutionReferenceCountAsync(cancellationToken);

        Check(
            "step 4 — before the held-back verification is let run, nothing has been committed",
            "0 snapshot records, no object under snapshots/{uploadId}/, the request still PENDING and no execution reference",
            $"{snapshots} snapshot record(s), {objects.Count} object(s) under the prefix, the request {intent?.Status ?? "(none)"} and {references} execution reference(s)",
            snapshots == 0
            && objects.Count == 0
            && intent is { Status: Pending }
            && references == 0);
    }

    // Step 4: the worker starts with the clock already at the deadline, claims the
    // request, runs the verification to its end, and the attempt writes nothing.
    private async Task LetTheHeldBackVerificationRunAsync(DateTimeOffset confirmedAt, CancellationToken cancellationToken)
    {
        _workerClient = _worker!.CreateClient();
        await WaitForHealthyAsync(_workerClient, "the worker's readiness", cancellationToken);

        var workflowId = UploadVerificationWorkflow.WorkflowIdFor(_uploadId!);
        var steps = 10;
        await Wait.UntilAsync(
            "the pending verification request to be claimed and dispatched",
            async token =>
            {
                var intent = await IntentAsync(token);
                var references = await ExecutionReferenceCountAsync(token);
                var claimed = intent is { Status: Dispatched } && references == 1;
                if (!claimed && steps > 0)
                {
                    // The dispatcher's poll is a timer on the run's own clock; a step moves it
                    // further past the deadline, which the case allows.
                    steps--;
                    _clock.Advance(TimeSpan.FromSeconds(1));
                }

                return claimed;
            },
            DispatchDeadline,
            PollInterval,
            cancellationToken);

        await Wait.UntilAsync(
            "the held-back verification to start and end",
            async token =>
            {
                var history = await HistoryAsync(workflowId, token);
                return ScheduledCount(history, UploadVerificationActivities.Name) == 1
                    && CompletedCount(history, UploadVerificationActivities.Name) == 1;
            },
            AttemptDeadline,
            PollInterval,
            cancellationToken);

        var result = await (await EngineClientAsync(cancellationToken))
            .GetWorkflowHandle(workflowId)
            .GetResultAsync<VerifyUploadResult>()
            .WaitAsync(AttemptDeadline, cancellationToken);

        var history = await HistoryAsync(workflowId, cancellationToken);
        var intent = await IntentAsync(cancellationToken);
        var references = await ExecutionReferenceCountAsync(cancellationToken);
        var snapshots = await SnapshotRecordCountAsync(cancellationToken);
        var objects = await SnapshotObjectsAsync(cancellationToken);
        var record = await UploadAsync(cancellationToken);
        var scheduled = ScheduledCount(history, UploadVerificationActivities.Name);
        var completed = CompletedCount(history, UploadVerificationActivities.Name);
        var workflowCompleted = history.Events.Any(entry => entry.EventType == EventType.WorkflowExecutionCompleted);

        Check(
            "step 4 — the attempt started at or past the deadline, once, and the workflow ran to its end",
            $"the request DISPATCHED at or after {UploadWireValues.FormatTimestamp(confirmedAt + VerificationDeadline)}; one execution reference; one {UploadVerificationActivities.Name} scheduled and completed; the execution completed",
            $"the request {intent?.Status ?? "(none)"} dispatched at {Wire(intent?.DispatchedAt)}; {references} execution reference(s); {scheduled} scheduled and {completed} completed {UploadVerificationActivities.Name}; the execution {(workflowCompleted ? "completed" : "did not complete")}",
            intent is { Status: Dispatched }
            && intent.DispatchedAt is { } dispatchedAt
            && new DateTimeOffset(DateTime.SpecifyKind(dispatchedAt, DateTimeKind.Utc)) >= confirmedAt + VerificationDeadline
            && references == 1
            && scheduled == 1
            && completed == 1
            && workflowCompleted);

        Check(
            "step 4 — the late verification commits nothing: the upload stays expired, no snapshot record is created and no object exists under snapshots/{uploadId}/",
            $"the attempt's result is {nameof(VerificationResult.NotVerifying)}; the record stays {StoredExpired}; 0 snapshot records; no object under the prefix; still one execution reference",
            $"the attempt's result is {result.Outcome}; the record reads {record?.Status ?? "(no record)"}; {snapshots} snapshot record(s); {objects.Count} object(s) under the prefix; {references} execution reference(s)",
            result.Outcome == VerificationResult.NotVerifying
            && record is { Status: StoredExpired }
            && snapshots == 0
            && objects.Count == 0
            && references == 1);
    }

    // Step 5: what the upload was holding is gone from the storage root.
    private async Task AssertTheStorageRootAsync(CancellationToken cancellationToken)
    {
        var objects = Objects();
        var staging = await objects.GetInfoAsync(
            ObjectKey.FromSegments("staging", _uploadId!, "content"),
            cancellationToken);
        var snapshots = await SnapshotObjectsAsync(cancellationToken);

        Check(
            "step 5 — the staging object is deleted and no snapshot object remains",
            $"the staging object reads {StorageOutcome.NotFound}; no object under snapshots/{{uploadId}}/",
            $"the staging object reads {staging.Outcome}; {snapshots.Count} object(s) under the prefix",
            staging.Outcome == StorageOutcome.NotFound && snapshots.Count == 0);
    }

    // Step 6: three repeated confirmations, and the differing declaration of the case.
    private async Task RepeatTheConfirmationsAsync(CancellationToken cancellationToken)
    {
        var first = await CompleteAsync(declaration: null, cancellationToken);
        var second = await CompleteAsync(declaration: null, cancellationToken);
        var repeated = await UploadAsync(cancellationToken);
        var snapshots = await SnapshotRecordCountAsync(cancellationToken);
        var references = await ExecutionReferenceCountAsync(cancellationToken);

        Check(
            "step 6 — a repeated confirmation with no declaration answers 200 with the current projection and records nothing",
            $"both answer 200 with status \"{Expired}\"; the record stays {StoredExpired}; 0 snapshots; still one execution reference",
            $"the first answers {(int)first.StatusCode} with status \"{first.Status}\" and the second {(int)second.StatusCode} with status \"{second.Status}\"; the record reads {repeated?.Status ?? "(no record)"}; {snapshots} snapshot record(s); {references} execution reference(s)",
            first.StatusCode == HttpStatusCode.OK
            && string.Equals(first.Status, Expired, StringComparison.Ordinal)
            && second.StatusCode == HttpStatusCode.OK
            && string.Equals(second.Status, Expired, StringComparison.Ordinal)
            && repeated is { Status: StoredExpired }
            && snapshots == 0
            && references == 1);

        // The first confirmation declared nothing, so "the same declaration" is no
        // declaration; a declaration is therefore a different one.
        var differing = await CompleteAsync(new string('a', 64), cancellationToken);
        var after = await UploadAsync(cancellationToken);
        var afterSnapshots = await SnapshotRecordCountAsync(cancellationToken);
        var afterReferences = await ExecutionReferenceCountAsync(cancellationToken);

        Check(
            "step 6 — a repeated confirmation with a different declaration answers 409 UPLOAD_STATE_CONFLICT and records nothing",
            $"409 with code {ErrorCodes.UploadStateConflict}; the record stays {StoredExpired}; 0 snapshots; still one execution reference",
            $"{(int)differing.StatusCode} with code {differing.Code ?? "(none)"}; the record reads {after?.Status ?? "(no record)"}; {afterSnapshots} snapshot record(s); {afterReferences} execution reference(s)",
            differing.StatusCode == HttpStatusCode.Conflict
            && string.Equals(differing.Code, ErrorCodes.UploadStateConflict, StringComparison.Ordinal)
            && after is { Status: StoredExpired }
            && afterSnapshots == 0
            && afterReferences == 1);
    }

    private async Task<CreatedUpload> CreateUploadAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                """{"profile":"header-items-v1","mode":"API_STREAM"}""",
                Encoding.UTF8,
                CreateUploadRequestReader.JsonMediaType),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await _apiClient!.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException(
                $"the upload could not be opened: {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return new CreatedUpload(
            root.GetProperty("uploadId").GetString()!,
            DateTimeOffset.Parse(WireTextOf(root, "createdAt")!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
            DateTimeOffset.Parse(WireTextOf(root, "expiresAt")!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
    }

    private async Task<(HttpStatusCode StatusCode, string? Status, string? CompletedAt, string? Code)> CompleteAsync(
        string? declaration,
        CancellationToken cancellationToken)
    {
        var body = declaration is null ? "{}" : $$"""{"sha256":"{{declaration}}"}""";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{UploadEndpoints.RoutePrefix}/{_uploadId}/complete", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, CreateUploadRequestReader.JsonMediaType),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await _apiClient!.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        return (
            response.StatusCode,
            WireTextOf(root, "status"),
            WireTextOf(root, "completedAt"),
            WireTextOf(root, "code"));
    }

    private async Task<(HttpStatusCode StatusCode, string? Status)> ReadStatusAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{UploadEndpoints.RoutePrefix}/{_uploadId}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await _apiClient!.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(content);
        return (response.StatusCode, WireTextOf(document.RootElement, "status"));
    }

    private async Task<UploadRow?> UploadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<UploadRow>(new CommandDefinition(
            """
            SELECT status               AS "Status",
                   created_at           AS "CreatedAt",
                   expires_at           AS "ExpiresAt",
                   completed_at         AS "CompletedAt",
                   verification_deadline AS "VerificationDeadline"
              FROM report_service.uploads
             WHERE upload_id = @UploadId
            """,
            new { UploadId = _uploadId },
            cancellationToken: cancellationToken));
    }

    private async Task<IntentRow?> IntentAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<IntentRow>(new CommandDefinition(
            """
            SELECT status          AS "Status",
                   next_attempt_at AS "NextAttemptAt",
                   dispatched_at   AS "DispatchedAt"
              FROM report_service.intents
             WHERE kind = 'start-upload-verification' AND subject_id = @UploadId
            """,
            new { UploadId = _uploadId },
            cancellationToken: cancellationToken));
    }

    private async Task<long> ExecutionReferenceCountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM report_service.execution_references WHERE workflow_id = @WorkflowId",
            new { WorkflowId = UploadVerificationWorkflow.WorkflowIdFor(_uploadId!) },
            cancellationToken: cancellationToken));
    }

    private async Task<long> SnapshotRecordCountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM report_service.snapshots WHERE upload_id = @UploadId",
            new { UploadId = _uploadId },
            cancellationToken: cancellationToken));
    }

    private async Task<List<string>> SnapshotObjectsAsync(CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        await foreach (var info in Objects().ListAsync($"snapshots/{_uploadId}", cancellationToken))
        {
            keys.Add(info.Key.ToString());
        }

        return keys;
    }

    private FilesystemObjectStore Objects() =>
        FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot() },
            StorageRoot(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);

    private Task<TemporalClient> EngineClientAsync(CancellationToken cancellationToken) =>
        TemporalClient
            .ConnectAsync(new TemporalClientConnectOptions($"127.0.0.1:{_engine!.HostPort.ToString(CultureInfo.InvariantCulture)}") { Namespace = "default" })
            .WaitAsync(cancellationToken);

    private async Task<WorkflowHistory> HistoryAsync(string workflowId, CancellationToken cancellationToken)
    {
        var client = await EngineClientAsync(cancellationToken);
        return await client.GetWorkflowHandle(workflowId)
            .FetchHistoryAsync(new WorkflowHistoryEventFetchOptions())
            .WaitAsync(cancellationToken);
    }

    private static int ScheduledCount(WorkflowHistory history, string activity) =>
        history.Events.Count(entry => entry.EventType == EventType.ActivityTaskScheduled
            && string.Equals(entry.ActivityTaskScheduledEventAttributes.ActivityType.Name, activity, StringComparison.Ordinal));

    private static int CompletedCount(WorkflowHistory history, string activity)
    {
        var scheduled = history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled
                && string.Equals(entry.ActivityTaskScheduledEventAttributes.ActivityType.Name, activity, StringComparison.Ordinal))
            .Select(entry => entry.EventId)
            .ToHashSet();
        return history.Events.Count(entry => entry.EventType == EventType.ActivityTaskCompleted
            && scheduled.Contains(entry.ActivityTaskCompletedEventAttributes.ScheduledEventId));
    }

    private Task WaitForHealthyAsync(HttpClient client, string what, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            what,
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

    // Every check writes its line before it asserts, so a failed assertion still leaves the
    // record of the step that failed and of every step before it.
    private void Check(string label, string expected, string observed, bool holds)
    {
        _record.Append(label).Append('\n')
            .Append("  expected : ").Append(expected).Append('\n')
            .Append("  observed : ").Append(observed).Append('\n')
            .Append("  result   : ").Append(holds ? "pass" : "fail").Append("\n\n");

        Assert.True(holds, $"{label}\n  expected: {expected}\n  observed: {observed}");
    }

    private void WriteRecord()
    {
        var text = new StringBuilder()
            .Append("verification deadline — the per-step record\n")
            .Append("clock start ").Append(UploadWireValues.FormatTimestamp(ClockStart))
            .Append("; verification deadline (d) ").Append(Duration(VerificationDeadline))
            .Append("; session lifetime ").Append(Duration(SessionLifetime))
            .Append("; the confirmation ").Append(Duration(ConfirmationDelay)).Append(" after the session opened")
            .Append('\n')
            .Append("upload ").Append(_uploadId ?? "(none)").Append('\n')
            .Append("confirmation ").Append(_confirmedAt == default ? "(not confirmed)" : UploadWireValues.FormatTimestamp(_confirmedAt)).Append('\n')
            .Append('\n')
            .Append(_record)
            .ToString();

        if (RunEnvironment.RunDirectory is null)
        {
            Console.WriteLine(text);
            return;
        }

        RunEnvironment.WriteEvidence("verification-deadline-steps.txt", Encoding.UTF8.GetBytes(text));
    }

    private static string? WireTextOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value.GetRawText(),
            }
            : null;

    private static string Wire(DateTime? value) =>
        value is { } instant
            ? UploadWireValues.FormatTimestamp(new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc)))
            : "(null)";

    private static string Duration(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);

    private string StorageRoot() => Path.Combine(_directory, "storage");

    private static byte[] ValidBody() =>
        Encoding.UTF8.GetBytes("""{"header":{},"items":[{"id":1,"v":17},{"id":2,"v":34}]}""");

    /// <summary>Whether a stored timestamp is the instant the run's clock reads.</summary>
    private static bool SameInstant(DateTime? stored, DateTimeOffset expected) =>
        stored is { } value && new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)) == expected;

    private sealed record CreatedUpload(string UploadId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

    /// <summary>One upload row, as the table holds it.</summary>
    private sealed class UploadRow
    {
        public string Status { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        public DateTime ExpiresAt { get; set; }

        public DateTime? CompletedAt { get; set; }

        public DateTime? VerificationDeadline { get; set; }
    }

    /// <summary>One start request, as the table holds it.</summary>
    private sealed class IntentRow
    {
        public string Status { get; set; } = string.Empty;

        public DateTime NextAttemptAt { get; set; }

        public DateTime? DispatchedAt { get; set; }
    }
}
