extern alias orchestration;

using System.IO.Compression;
using System.Globalization;
using System.Net;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.Testing;
using ReportService.Uploads;
using Temporalio.Client;
using Temporalio.Common;
using OrchestrationWorkerProgram = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;
using UploadExpiryOptions = orchestration::ReportService.Workers.Orchestration.Uploads.UploadExpiryOptions;
using UploadVerificationActivityOptions = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationActivityOptions;
using UploadVerificationInput = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationInput;
using UploadVerificationOptions = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationOptions;
using UploadVerificationWorkflow = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationWorkflow;
using VerificationOptions = orchestration::ReportService.Workers.Orchestration.Uploads.VerificationOptions;
using VerificationResult = orchestration::ReportService.Workers.Orchestration.Uploads.VerificationResult;

namespace ReportService.IntegrationTests.Uploads;

/// <summary>
/// The end of an upload's life on a real PostgreSQL server: the constraints that
/// keep a confirmed upload from standing without a deadline, the batch expiry that
/// two callers may run at once without expiring the same upload twice, the back-fill
/// that gives the uploads confirmed before the script a deadline of their own, and
/// the sweep that ends an upload whose verification never got the chance to run.
/// </summary>
public sealed class UploadVerificationDeadlineTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource _dataSource;

    /// <summary>Brings a database of its own to the version the code expects.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public UploadVerificationDeadlineTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _dataSource = null!;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var database = await _fixture.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        var migration = await MigrationRunner.RunAsync(
            ConnectionString(database), MigrationCatalog.Scripts, TestContext.Current.CancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(ConnectionString(database));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();

    [Fact]
    public async Task AVerifyingUploadWithoutADeadlineIsRefusedByTheSchema()
    {
        await using var connection = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            """
            INSERT INTO report_service.uploads
                (upload_id, consumer_id, mode, declared_profile, status, staging_key, created_at, expires_at, completed_at)
            VALUES
                (@UploadId, 'dev-consumer', 'API_STREAM', 'HEADER_ITEMS_V1', 'VERIFYING',
                 'staging/x/content', @CreatedAt, @ExpiresAt, @CompletedAt)
            """,
            new
            {
                UploadId = "upload-00000000000000000000000000000001",
                CreatedAt = Now.UtcDateTime,
                ExpiresAt = Now.AddDays(1).UtcDateTime,
                CompletedAt = Now.UtcDateTime,
            }));

        Assert.Equal("23514", failure.SqlState);
        Assert.Equal("uploads_deadline_when_verifying", failure.ConstraintName);
    }

    [Fact]
    public async Task ADeadlineBeforeTheConfirmationIsRefusedByTheSchema()
    {
        await using var connection = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            """
            INSERT INTO report_service.uploads
                (upload_id, consumer_id, mode, declared_profile, status, staging_key, created_at, expires_at,
                 completed_at, verification_deadline)
            VALUES
                (@UploadId, 'dev-consumer', 'API_STREAM', 'HEADER_ITEMS_V1', 'VERIFYING',
                 'staging/x/content', @CreatedAt, @ExpiresAt, @CompletedAt, @Deadline)
            """,
            new
            {
                UploadId = "upload-00000000000000000000000000000002",
                CreatedAt = Now.UtcDateTime,
                ExpiresAt = Now.AddDays(1).UtcDateTime,
                CompletedAt = Now.UtcDateTime,
                Deadline = Now.AddMinutes(-1).UtcDateTime,
            }));

        Assert.Equal("23514", failure.SqlState);
        Assert.Equal("uploads_deadline_after_confirmation", failure.ConstraintName);
    }

    [Fact]
    public async Task ApplyingTheDeadlineScriptGivesTheUploadsAlreadyVerifyingADeadlineOfTheirOwn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // A database as it stood before the script, holding the two states that matter here.
        var database = await _fixture.CreateDatabaseAsync(cancellationToken);
        var connectionString = ConnectionString(database);
        var beforeTheScript = MigrationCatalog.Scripts
            .Where(script => string.CompareOrdinal(script.Name, "0004") < 0)
            .ToList();
        var applied = await MigrationRunner.RunAsync(connectionString, beforeTheScript, cancellationToken);
        Assert.True(applied.Succeeded, string.Join("; ", applied.ErrorLines));
        Assert.Equal(beforeTheScript[^1].Name, applied.CurrentVersion);

        const string Verifying = "upload-00000000000000000000000000000003";
        const string Created = "upload-00000000000000000000000000000004";
        var confirmedAt = Now.AddMinutes(-30);
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO report_service.uploads
                    (upload_id, consumer_id, mode, declared_profile, status, staging_key, created_at, expires_at, completed_at)
                VALUES
                    (@Verifying, 'dev-consumer', 'API_STREAM', 'HEADER_ITEMS_V1', 'VERIFYING',
                     'staging/verifying/content', @CreatedAt, @ExpiresAt, @CompletedAt),
                    (@Created, 'dev-consumer', 'API_STREAM', 'HEADER_ITEMS_V1', 'CREATED',
                     'staging/created/content', @CreatedAt, @ExpiresAt, NULL)
                """,
                new
                {
                    Verifying,
                    Created,
                    CreatedAt = Now.AddHours(-1).UtcDateTime,
                    ExpiresAt = Now.AddDays(1).UtcDateTime,
                    CompletedAt = confirmedAt.UtcDateTime,
                },
                cancellationToken: cancellationToken));
        }

        // The script alone brings the schema to the expected version and fills the gap it created.
        var migration = await MigrationRunner.RunAsync(connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        Assert.Equal(MigrationCatalog.ExpectedVersion, migration.CurrentVersion);

        await using var after = new NpgsqlConnection(connectionString);
        await after.OpenAsync(cancellationToken);
        var backFilled = await after.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
            "SELECT verification_deadline FROM report_service.uploads WHERE upload_id = @Verifying",
            new { Verifying },
            cancellationToken: cancellationToken));
        var untouched = await after.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
            "SELECT verification_deadline FROM report_service.uploads WHERE upload_id = @Created",
            new { Created },
            cancellationToken: cancellationToken));

        Assert.Equal(confirmedAt.AddHours(6).UtcDateTime, backFilled);
        Assert.Null(untouched);
    }

    [Fact]
    public async Task TwoConcurrentBatchExpiriesExpireDisjointSetsThatCoverEveryOverdueUpload()
    {
        var store = new PostgresUploadStore(_dataSource);
        var cancellationToken = TestContext.Current.CancellationToken;
        var overdue = new List<string>();
        for (var index = 0; index < 20; index++)
        {
            var uploadId = "upload-" + index.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + new string('0', 24);
            overdue.Add(uploadId);
            await store.CreateAsync(
                new UploadRecord(
                    uploadId,
                    "dev-consumer",
                    UploadMode.ApiStream,
                    DataProfile.HeaderItemsV1,
                    UploadStatus.Created,
                    ReportService.Storage.ObjectKey.FromSegments("staging", uploadId, "content"),
                    Now.AddHours(-2),
                    Now),
                cancellationToken);
        }

        var first = store.ExpireOverdueAsync(Now, 100, cancellationToken);
        var second = store.ExpireOverdueAsync(Now, 100, cancellationToken);
        var results = await Task.WhenAll(first, second);

        var expired = results.SelectMany(batch => batch.Select(upload => upload.UploadId)).ToList();
        Assert.Equal(expired.Count, expired.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(overdue.Order(StringComparer.Ordinal), expired.Order(StringComparer.Ordinal));
        Assert.All(results.SelectMany(batch => batch), upload => Assert.Equal(UploadStatus.Created, upload.PreviousStatus));

        var again = await store.ExpireOverdueAsync(Now, 100, cancellationToken);
        Assert.Empty(again);
    }

    [Fact]
    public async Task ASweepEndsAnUploadWhoseDeadlinePassesBeforeItsVerificationCanRun()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await _fixture.CreateDatabaseAsync(cancellationToken);
        var connectionString = ConnectionString(database);
        var migration = await MigrationRunner.RunAsync(connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));

        var root = Path.Combine(
            Path.GetTempPath(),
            "report-service-tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);

        var image = CompositionPins.Load().WorkflowEngine;
        try
        {
            var engine = await EngineStart.StartAsync(image, "temporal-upload-deadline", cancellationToken);
            await using var engineOwner = engine.Container;
            var enginePort = engine.HostPort;

            const string UploadId = "upload-00000000000000000000000000000005";
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await GivenAnUploadWhoseDeadlineIsPassingAsync(dataSource, root, UploadId, cancellationToken);

            // The worker's own registrations with the sweep in process, and the data queue's engine
            // worker left out of them: the upload's deadline passes while no attempt can run. This
            // host keeps the orchestration queue's work, so its engine worker serves the workflow
            // that runs once the deadline has passed.
            await using var orchestration = Worker(enginePort, connectionString, root, UploadVerificationWorkflow.OrchestrationQueue);
            using (var orchestrationHealth = orchestration.CreateClient())
            {
                await WaitForHealthyAsync(orchestrationHealth, cancellationToken);
            }

            await Wait.UntilAsync(
                "the sweep to end the upload whose verification never ran",
                async token => await StatusOfAsync(dataSource, UploadId, token) == "EXPIRED",
                SweepDeadline,
                TimeSpan.FromMilliseconds(250),
                cancellationToken);

            // The sweep ended it and deleted what it was still holding.
            Assert.Equal(0L, await SnapshotRecordsAsync(dataSource, UploadId, cancellationToken));
            var objects = FilesystemObjectStore.Open(
                new FilesystemStorageOptions { Root = root },
                root,
                TimeProvider.System,
                NullLogger<FilesystemObjectStore>.Instance);
            Assert.Equal(
                StorageOutcome.NotFound,
                (await objects.GetInfoAsync(ObjectKey.FromSegments("staging", UploadId, "content"), cancellationToken)).Outcome);

            // The data worker starts; the verification it now serves finds the upload over and
            // commits nothing.
            await using var data = Worker(enginePort, connectionString, root, UploadVerificationActivityOptions.DataQueue);
            using (var dataHealth = data.CreateClient())
            {
                await WaitForHealthyAsync(dataHealth, cancellationToken);
            }

            var client = await ConnectAsync(enginePort, cancellationToken);
            var handle = await client.StartWorkflowAsync(
                (UploadVerificationWorkflow workflow) => workflow.RunAsync(new UploadVerificationInput(UploadId)),
                new WorkflowOptions
                {
                    Id = UploadVerificationWorkflow.WorkflowIdFor(UploadId),
                    TaskQueue = UploadVerificationWorkflow.OrchestrationQueue,
                });
            var result = await handle.GetResultAsync().WaitAsync(RunDeadline, cancellationToken);

            Assert.Equal(VerificationResult.NotVerifying, result.Outcome);
            Assert.Equal("EXPIRED", await StatusOfAsync(dataSource, UploadId, cancellationToken));
            Assert.Equal(0L, await SnapshotRecordsAsync(dataSource, UploadId, cancellationToken));
            var attemptObjects = new List<string>();
            await foreach (var info in objects.ListAsync($"snapshots/{UploadId}", cancellationToken))
            {
                attemptObjects.Add(info.Key.ToString());
            }

            Assert.Empty(attemptObjects);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not a test failure.
            }
        }
    }

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan SweepDeadline = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan RunDeadline = TimeSpan.FromMinutes(1);

    /// <summary>How long after its confirmation the upload of this case may still fix a snapshot.</summary>
    private static readonly TimeSpan VerificationDeadline = TimeSpan.FromSeconds(3);

    /// <summary>How long one verification attempt may take, under the deadline above.</summary>
    private static readonly TimeSpan StartToClose = TimeSpan.FromSeconds(2);

    /// <summary>How often the sweep of this case looks for uploads that ran out of time.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    // One process of the worker, with only one of its two declared queues carrying work: its
    // settings, the metadata database and one storage root.
    private WebApplicationFactory<OrchestrationWorkerProgram> Worker(
        int enginePort,
        string connectionString,
        string root,
        string queue) =>
        new WebApplicationFactory<OrchestrationWorkerProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{enginePort.ToString(CultureInfo.InvariantCulture)}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.UseSetting($"{ObjectStorageSettings.SectionPath}:{nameof(ObjectStorageSettings.Provider)}", FilesystemObjectStore.ProviderName);
            builder.UseSetting($"{FilesystemStorageOptions.SectionPath}:{nameof(FilesystemStorageOptions.Root)}", root);
            builder.UseSetting($"{FilesystemStorageOptions.SectionPath}:{nameof(FilesystemStorageOptions.CreateIfMissing)}", "true");
            builder.UseSetting(
                $"{UploadVerificationOptions.SectionPath}:{UploadVerificationOptions.StartToCloseSetting}",
                Duration(StartToClose));
            builder.UseSetting(
                $"{VerificationOptions.SectionPath}:{VerificationOptions.VerificationDeadlineSetting}",
                Duration(VerificationDeadline));
            builder.UseSetting(
                $"{UploadExpiryOptions.SectionPath}:{UploadExpiryOptions.SweepIntervalSetting}",
                Duration(SweepInterval));
            builder.ConfigureTestServices(services => KeepOnlyQueueWork(services, queue));
        });

    // A process creates one engine worker per declared queue that has work, and applies every
    // registration of that queue to it. This case needs the two halves of that process at two
    // moments, so it keeps only the wanted queue's work - every registration of it, which is what
    // the process itself would apply - and each host starts exactly the engine worker its moment
    // calls for; the product keeps no test switch. The work is registered by the product's own
    // factories, which build the registration and only then state its queue, so the wanted ones
    // are picked after they are built, and a queue with no work at all fails here instead of
    // silently leaving a second worker of this process. `report-orchestration` carries two
    // registrations - the verification workflow and the jobs' work, the notification activity
    // among them - and a host that kept one of them would serve the workflow and not what it
    // schedules next.
    private static void KeepOnlyQueueWork(IServiceCollection services, string queue)
    {
        var registrations = services
            .Where(descriptor => descriptor.ServiceType == typeof(TaskQueueWork)
                && descriptor.ImplementationFactory is not null)
            .Select(descriptor => descriptor.ImplementationFactory!)
            .ToList();
        services.RemoveAll<TaskQueueWork>();
        services.AddSingleton(provider =>
        {
            var kept = registrations
                .Select(factory => factory(provider))
                .OfType<TaskQueueWork>()
                .Where(work => string.Equals(work.Queue, queue, StringComparison.Ordinal))
                .ToList();
            if (kept.Count == 0)
            {
                throw new InvalidOperationException($"No work is registered on the task queue '{queue}'.");
            }

            return new TaskQueueWork(queue, options =>
            {
                foreach (var work in kept)
                {
                    work.Configure(options);
                }
            });
        });
    }

    private static string Duration(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);

    // The state a confirmation leaves, recorded without the durable request that would have the
    // dispatcher start the verification before the deadline: this case starts the workflow itself,
    // once the data worker serves its queue.
    private static async Task GivenAnUploadWhoseDeadlineIsPassingAsync(
        NpgsqlDataSource dataSource,
        string root,
        string uploadId,
        CancellationToken cancellationToken)
    {
        const string Consumer = "dev-consumer";
        var store = new PostgresUploadStore(dataSource);
        var stagingKey = ObjectKey.FromSegments("staging", uploadId, "content");
        var objects = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = root },
            root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var now = UploadWireValues.TruncateToMillisecond(DateTimeOffset.UtcNow);
        using (var content = new MemoryStream(Gzip(Content()), writable: false))
        {
            var written = await objects.WriteAsync(stagingKey, content, cancellationToken);
            Assert.True(written.Succeeded, "the staging object had to be written");
            await store.CreateAsync(
                new UploadRecord(
                    uploadId,
                    Consumer,
                    UploadMode.ApiStream,
                    DataProfile.HeaderItemsV1,
                    UploadStatus.Created,
                    stagingKey,
                    now,
                    now.AddHours(1)),
                cancellationToken);
            Assert.True(await store.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.Gzip, now, cancellationToken));
            Assert.True(await store.RecordTransferAsync(
                uploadId,
                UploadContentOutcome.Complete,
                written.Value!.Size,
                written.Value.Size,
                written.Value.Sha256,
                now,
                cancellationToken));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var confirmed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE report_service.uploads
               SET status = 'VERIFYING', completed_at = @CompletedAt, verification_deadline = @Deadline
             WHERE upload_id = @UploadId AND status = 'UPLOADING' AND content_outcome IS NOT NULL
            """,
            new
            {
                UploadId = uploadId,
                CompletedAt = now.UtcDateTime,
                Deadline = now.Add(VerificationDeadline).UtcDateTime,
            },
            cancellationToken: cancellationToken));
        Assert.Equal(1, confirmed);
    }

    private static async Task<string> StatusOfAsync(NpgsqlDataSource dataSource, string uploadId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT status FROM report_service.uploads WHERE upload_id = @UploadId",
            new { UploadId = uploadId },
            cancellationToken: cancellationToken));
    }

    private static async Task<long> SnapshotRecordsAsync(NpgsqlDataSource dataSource, string uploadId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM report_service.snapshots WHERE upload_id = @UploadId",
            new { UploadId = uploadId },
            cancellationToken: cancellationToken));
    }

    private static Task WaitForHealthyAsync(HttpClient client, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            "the worker's readiness",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

    private static Task<TemporalClient> ConnectAsync(int enginePort, CancellationToken cancellationToken) =>
        TemporalClient
            .ConnectAsync(new TemporalClientConnectOptions($"127.0.0.1:{enginePort.ToString(CultureInfo.InvariantCulture)}") { Namespace = "default" })
            .WaitAsync(cancellationToken);

    // A small body of the shape a verification accepts, compressed on the spot.
    private static byte[] Content()
    {
        var builder = new System.Text.StringBuilder("""{"header":{},"items":[""");
        for (var index = 0; index < 64; index++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{{\"id\":{index},\"v\":{index * 17}}},");
        }

        builder.Append("]}");
        return System.Text.Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static byte[] Gzip(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    private string ConnectionString(string database) => new NpgsqlConnectionStringBuilder(_fixture.Application(database))
    {
        Timeout = 5,
        CommandTimeout = PostgresUploadStore.CommandTimeoutSeconds,
    }.ConnectionString;
}
