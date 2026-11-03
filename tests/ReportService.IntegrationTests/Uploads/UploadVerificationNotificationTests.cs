extern alias orchestration;

using System.Globalization;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.Testing;
using ReportService.Uploads;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Worker;
using InputWaitSignalActivities = orchestration::ReportService.Workers.Orchestration.Jobs.InputWaitSignalActivities;
using IWorkflowSignaller = orchestration::ReportService.Workers.Orchestration.Jobs.IWorkflowSignaller;
using UploadVerificationActivities = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationActivities;
using UploadVerificationActivityOptions = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationActivityOptions;
using UploadVerificationInput = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationInput;
using UploadVerificationWorkflow = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationWorkflow;
using VerificationOptions = orchestration::ReportService.Workers.Orchestration.Uploads.VerificationOptions;
using VerificationResult = orchestration::ReportService.Workers.Orchestration.Uploads.VerificationResult;
using UploadVerifier = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerifier;
using OrchestrationMetrics = orchestration::ReportService.Workers.Orchestration.Telemetry.OrchestrationMetrics;

namespace ReportService.IntegrationTests.Uploads;

/// <summary>
/// A finished verification tells the jobs waiting on its upload that the state changed: the
/// jobs of that upload are nudged through the engine connection, a job of another upload is
/// not, a job that already ended is not, and an upload with nothing waiting completes exactly
/// as before. The verification workflow and the notification step are the delivered ones, the
/// job records are the product's own rows in PostgreSQL, and the engine is the pinned
/// development server in its own container.
/// </summary>
public sealed class UploadVerificationNotificationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const int GrpcPort = 7233;

    private const string Consumer = "dev-consumer";

    private static readonly TimeSpan EngineStartDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan ExecutionDeadline = TimeSpan.FromMinutes(2);

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _image = CompositionPins.Load().WorkflowEngine;

    private readonly int _hostPort = FreePort();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "report-service-notification",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private IContainer? _engine;

    private NpgsqlDataSource? _dataSource;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public UploadVerificationNotificationTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        var database = await _fixture.CreateDatabaseAsync(cancellationToken);
        var connectionString = _fixture.Application(database);
        var migration = await MigrationRunner.RunAsync(connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(connectionString);

        _engine = ContainerSmoke.Builder(_image, "temporal-notification")
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0")
            .WithPortBinding(_hostPort, GrpcPort)
            .Build();
        await ContainerSmoke.StartAsync(_engine, _image, cancellationToken);
        await Wait.UntilAsync(
            $"Temporal gRPC ({_image.Reference}) on port {_hostPort}",
            async token =>
            {
                var probe = await ClientAsync(token);
                return await probe.Connection.CheckHealthAsync(options: new RpcOptions { CancellationToken = token });
            },
            EngineStartDeadline,
            TimeSpan.FromSeconds(1),
            cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    [Fact]
    public async Task OnlyTheWaitingJobsOfTheVerifiedUploadAreNudged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (uploadId, otherUploadId) = await GivenTwoConfirmedUploadsAsync(cancellationToken);
        var signaller = new RecordingSignaller();

        // One job of the verified upload waits, one of it already ended, and one waits on
        // another upload: only the first may be nudged.
        var waiting = await AcceptAsync(uploadId, cancellationToken);
        var ended = await AcceptAsync(uploadId, cancellationToken);
        var foreign = await AcceptAsync(otherUploadId, cancellationToken);
        var store = new PostgresJobStore(_dataSource!);
        Assert.Equal(
            BeginInputWaitOutcome.Applied,
            await store.BeginInputWaitAsync(waiting, cancellationToken));
        await store.BeginInputWaitAsync(foreign, cancellationToken);
        Assert.Equal(
            JobFailureOutcome.Applied,
            await store.FailJobAsync(ended, "INPUT_NOT_FOUND", "input-expired", DateTimeOffset.UtcNow, cancellationToken));

        var result = await VerifyAsync(uploadId, store, signaller, cancellationToken);

        Assert.Equal(VerificationResult.Ready, result);
        Assert.Equal([UploadVerificationWorkflow.WorkflowIdFor(waiting)], signaller.Signalled);
        Assert.DoesNotContain(UploadVerificationWorkflow.WorkflowIdFor(foreign), signaller.Signalled);
        Assert.DoesNotContain(UploadVerificationWorkflow.WorkflowIdFor(ended), signaller.Signalled);
    }

    [Fact]
    public async Task AVVerificationWithNothingWaitingCompletesUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (uploadId, _) = await GivenTwoConfirmedUploadsAsync(cancellationToken);
        var signaller = new RecordingSignaller();

        var result = await VerifyAsync(uploadId, new PostgresJobStore(_dataSource!), signaller, cancellationToken);

        Assert.Equal(VerificationResult.Ready, result);
        Assert.Empty(signaller.Signalled);
    }

    private async Task<VerificationResult> VerifyAsync(
        string uploadId,
        PostgresJobStore store,
        IWorkflowSignaller signaller,
        CancellationToken cancellationToken)
    {
        using var metrics = new OrchestrationMetrics();
        var objects = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = _root, CreateIfMissing = true },
            _root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var uploads = new PostgresUploadStore(_dataSource!);
        var verification = new UploadVerificationActivities(
            new UploadVerifier(
                uploads,
                objects,
                Options.Create(new VerificationOptions { MaxStoredBytes = 8 << 20, MaxUncompressedBytes = 8 << 20 }),
                new FixedTimeProvider(Now),
                NullLogger<UploadVerifier>.Instance,
                metrics),
            TimeProvider.System);
        var notification = new InputWaitSignalActivities(
            store,
            signaller,
            metrics,
            NullLogger<InputWaitSignalActivities>.Instance);

        var client = await ClientAsync(cancellationToken);
        using var workflowWorker = new TemporalWorker(
            client,
            new TemporalWorkerOptions(UploadVerificationWorkflow.OrchestrationQueue)
                .AddWorkflow<UploadVerificationWorkflow>()
                .AddAllActivities(notification));
        using var activityWorker = new TemporalWorker(
            client,
            new TemporalWorkerOptions(UploadVerificationActivityOptions.DataQueue)
                .AddAllActivities(verification));

        using var stop = new CancellationTokenSource();
        var runs = new[] { workflowWorker.ExecuteAsync(stop.Token), activityWorker.ExecuteAsync(stop.Token) };
        try
        {
            var handle = await client.StartWorkflowAsync(
                (UploadVerificationWorkflow workflow) => workflow.RunAsync(new UploadVerificationInput(uploadId)),
                new WorkflowOptions
                {
                    Id = UploadVerificationWorkflow.WorkflowIdFor(uploadId),
                    TaskQueue = UploadVerificationWorkflow.OrchestrationQueue,
                });
            return (await handle.GetResultAsync().WaitAsync(ExecutionDeadline, cancellationToken)).Outcome;
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAny(Task.WhenAll(runs), Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        }
    }

    private async Task<(string UploadId, string OtherUploadId)> GivenTwoConfirmedUploadsAsync(CancellationToken cancellationToken)
    {
        var first = await GivenConfirmedUploadAsync(cancellationToken);
        var second = await GivenConfirmedUploadAsync(cancellationToken);
        return (first, second);
    }

    private async Task<string> GivenConfirmedUploadAsync(CancellationToken cancellationToken)
    {
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        var objects = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = _root, CreateIfMissing = true },
            _root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var content = System.Text.Encoding.UTF8.GetBytes("""{"header":{},"items":[{"id":1,"v":17}]}""");
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(content);
        }

        var stored = buffer.ToArray();
        var stagingKey = ObjectKey.FromSegments("staging", uploadId, "content");
        var written = await objects.WriteAsync(stagingKey, new MemoryStream(stored, writable: false), cancellationToken);
        Assert.True(written.Succeeded, "the body of the case had to be staged");

        var uploads = new PostgresUploadStore(_dataSource!);
        var record = new UploadRecord(
            uploadId,
            Consumer,
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Created,
            stagingKey,
            Now,
            Now.AddDays(1));
        await uploads.CreateAsync(record, cancellationToken);
        Assert.True(await uploads.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.Gzip, Now, cancellationToken));
        Assert.True(await uploads.RecordTransferAsync(
            uploadId,
            UploadContentOutcome.Complete,
            stored.Length,
            stored.Length,
            Convert.ToHexStringLower(SHA256.HashData(stored)),
            Now,
            cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, Consumer, null, Now, Now, Now.AddHours(6), cancellationToken));
        return uploadId;
    }

    private async Task<string> AcceptAsync(string uploadId, CancellationToken cancellationToken)
    {
        var store = new PostgresJobStore(_dataSource!);
        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            JobIdentifier.CreateOutput(),
            Consumer,
            uploadId,
            "pilot-grouped",
            TemplateReferenceForm.Exact,
            "1.0.0");
        var accepted = await store.AcceptAsync(acceptance, DateTimeOffset.UtcNow, cancellationToken);
        Assert.IsType<JobAcceptanceResult.Accepted>(accepted);
        return acceptance.ReportJobId;
    }

    private Task<TemporalClient> ClientAsync(CancellationToken cancellationToken) =>
        TemporalClient.ConnectAsync(new TemporalClientConnectOptions($"127.0.0.1:{_hostPort}") { Namespace = "default" })
            .WaitAsync(cancellationToken);

    private static int FreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The seam, doubled: it remembers what it nudged and answers that every execution exists.</summary>
    private sealed class RecordingSignaller : IWorkflowSignaller
    {
        public List<string> Signalled { get; } = [];

        public Task<bool> SignalInputChangedAsync(string workflowId, CancellationToken cancellationToken)
        {
            Signalled.Add(workflowId);
            return Task.FromResult(true);
        }
    }
}
