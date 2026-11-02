extern alias orchestration;

using System.Globalization;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.Testing;
using ReportService.Uploads;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Worker;
using OrchestrationMetrics = orchestration::ReportService.Workers.Orchestration.Telemetry.OrchestrationMetrics;
using UploadVerifier = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerifier;
using UploadVerificationActivities = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationActivities;
using UploadVerificationActivityOptions = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationActivityOptions;
using UploadVerificationInput = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationInput;
using UploadVerificationWorkflow = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerificationWorkflow;
using VerificationOptions = orchestration::ReportService.Workers.Orchestration.Uploads.VerificationOptions;
using VerificationResult = orchestration::ReportService.Workers.Orchestration.Uploads.VerificationResult;
using VerifyUploadResult = orchestration::ReportService.Workers.Orchestration.Uploads.VerifyUploadResult;

namespace ReportService.IntegrationTests.Uploads;

/// <summary>
/// Records one verification history against a real engine, once, so the hermetic
/// suite has something to replay. It is opt-in: it runs only when the environment
/// variable names the file to write, so the integration command never runs it, and
/// the ordinary cases of this suite are untouched by it. It needs one engine
/// container and nothing else: the upload it verifies lives in memory and on a
/// temporary storage root the case removes, so the history is the outcome of a real
/// execution of the workflow and the attempt it runs.
/// </summary>
public sealed class UploadVerificationHistoryCapture : IAsyncLifetime
{
    /// <summary>The variable that names the file the captured history is written to; without it the case is skipped.</summary>
    public const string OutputVariable = "REPORTSERVICE_CAPTURE_WORKFLOW_HISTORY";

    private const int GrpcPort = 7233;

    private const string UploadId = "upload-0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero);

    private static readonly DateTimeOffset ConfirmedAt = new(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero);

    private static readonly TimeSpan EngineStartDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan ExecutionDeadline = TimeSpan.FromMinutes(2);

    private readonly PinnedImage _image = CompositionPins.Load().WorkflowEngine;

    private IContainer? _engine;

    private string? _root;

    private int _hostPort;

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }

        if (_root is not null)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not a test failure.
            }
        }
    }

    [Fact]
    public async Task OneVerificationAgainstARealEngineIsRecordedAsTheHistoryTheSuiteReplays()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(target))
        {
            Assert.Skip($"the capture runs once, when {OutputVariable} names the file to write.");
        }

        using var metrics = new OrchestrationMetrics();
        var uploads = new InMemoryUploadStore();
        _root = Path.Combine(
            Path.GetTempPath(),
            "report-service-tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_root);
        var objects = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = _root },
            _root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var activities = new UploadVerificationActivities(
            new UploadVerifier(
                uploads,
                objects,
                Options.Create(new VerificationOptions { MaxStoredBytes = 8 << 20, MaxUncompressedBytes = 8 << 20 }),
                new FixedTimeProvider(ConfirmedAt),
                NullLogger<UploadVerifier>.Instance,
                metrics),
            TimeProvider.System);

        await GivenAConfirmedUploadAsync(uploads, objects, cancellationToken);

        await StartEngineAsync(cancellationToken);
        var client = await ConnectAsync(cancellationToken);
        using var workflowWorker = new TemporalWorker(
            client,
            new TemporalWorkerOptions(UploadVerificationWorkflow.OrchestrationQueue)
                .AddWorkflow<UploadVerificationWorkflow>());
        using var activityWorker = new TemporalWorker(
            client,
            new TemporalWorkerOptions(UploadVerificationActivityOptions.DataQueue)
                .AddAllActivities(activities));

        using var stopWorkers = new CancellationTokenSource();
        var workflowWorkerRun = workflowWorker.ExecuteAsync(stopWorkers.Token);
        var activityWorkerRun = activityWorker.ExecuteAsync(stopWorkers.Token);
        try
        {
            var handle = await client.StartWorkflowAsync(
                (UploadVerificationWorkflow workflow) => workflow.RunAsync(new UploadVerificationInput(UploadId)),
                new WorkflowOptions
                {
                    Id = UploadVerificationWorkflow.WorkflowIdFor(UploadId),
                    TaskQueue = UploadVerificationWorkflow.OrchestrationQueue,
                });
            var result = await handle.GetResultAsync().WaitAsync(ExecutionDeadline, cancellationToken);
            Assert.Equal(VerificationResult.Ready, result.Outcome);

            var history = await handle.FetchHistoryAsync(new WorkflowHistoryEventFetchOptions())
                .WaitAsync(ExecutionDeadline, cancellationToken);
            var directory = Path.GetDirectoryName(Path.GetFullPath(target!));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(target!, history.ToJson(), cancellationToken);
        }
        finally
        {
            await stopWorkers.CancelAsync();
            foreach (var worker in new[] { workflowWorkerRun, activityWorkerRun })
            {
                try
                {
                    await worker;
                }
                catch (OperationCanceledException)
                {
                    // The worker was asked to stop, which is how a run of one ends.
                }
            }
        }
    }

    private async Task GivenAConfirmedUploadAsync(
        InMemoryUploadStore uploads,
        FilesystemObjectStore objects,
        CancellationToken cancellationToken)
    {
        var content = Content();
        var stagingKey = ObjectKey.FromSegments("staging", UploadId, "content");
        using var source = new MemoryStream(content, writable: false);
        var written = await objects.WriteAsync(stagingKey, source, cancellationToken);
        Assert.True(written.Succeeded, "the staging object of the capture had to be written");

        var record = new UploadRecord(
            UploadId,
            "dev-consumer",
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Created,
            stagingKey,
            CreatedAt,
            CreatedAt.AddDays(1));
        await uploads.CreateAsync(record, cancellationToken);
        await uploads.TryStartTransferAsync(UploadId, record.ConsumerId, UploadContentEncoding.Gzip, ConfirmedAt, cancellationToken);
        await uploads.RecordTransferAsync(
            UploadId,
            UploadContentOutcome.Complete,
            written.Value!.Size,
            written.Value.Size,
            written.Value.Sha256,
            ConfirmedAt,
            cancellationToken);
        await uploads.ConfirmAsync(
            UploadId, record.ConsumerId, null, ConfirmedAt, ConfirmedAt, ConfirmedAt.AddHours(6), cancellationToken);
    }

    private async Task StartEngineAsync(CancellationToken cancellationToken)
    {
        _hostPort = FreePort();
        _engine = ContainerSmoke.Builder(_image, "temporal-capture")
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0")
            .WithPortBinding(_hostPort, GrpcPort)
            .Build();
        await ContainerSmoke.StartAsync(_engine, _image, cancellationToken);
        await Wait.UntilAsync(
            $"Temporal gRPC ({_image.Reference}) on port {_hostPort}",
            async token =>
            {
                var probe = await ConnectAsync(token);
                return await probe.Connection.CheckHealthAsync(options: new RpcOptions { CancellationToken = token });
            },
            EngineStartDeadline,
            TimeSpan.FromSeconds(1),
            cancellationToken);
    }

    private Task<TemporalClient> ConnectAsync(CancellationToken cancellationToken) =>
        TemporalClient.ConnectAsync(new TemporalClientConnectOptions($"127.0.0.1:{_hostPort}") { Namespace = "default" })
            .WaitAsync(cancellationToken);

    // A small, compressible body of the shape the product verifies, compressed on the
    // spot so the captured run covers the decompressing path as well.
    private static byte[] Content()
    {
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(
            buffer,
            System.IO.Compression.CompressionLevel.Fastest,
            leaveOpen: true))
        {
            var block = System.Text.Encoding.UTF8.GetBytes("{\"header\":{},\"items\":[");
            gzip.Write(block);
            for (var index = 0; index < 512; index++)
            {
                gzip.Write(System.Text.Encoding.UTF8.GetBytes(
                    string.Create(CultureInfo.InvariantCulture, $"{{\"id\":{index},\"v\":{index * 17}}},")));
            }

            gzip.Write(System.Text.Encoding.UTF8.GetBytes("]}"));
        }

        return buffer.ToArray();
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
