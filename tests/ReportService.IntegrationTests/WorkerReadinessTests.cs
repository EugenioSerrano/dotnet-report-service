extern alias orchestration;

using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Testing;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.TaskQueue.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using OrchestrationWorker = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;

namespace ReportService.IntegrationTests;

/// <summary>
/// The orchestration worker against a real Temporal development server: it is
/// ready only once connected, each declared queue is then polled by exactly one
/// engine worker, it is not ready while the engine is gone and ready again when
/// it returns — with the same process. The class owns its engine containers.
/// </summary>
public sealed class WorkerReadinessTests : IAsyncLifetime
{
    private const int GrpcPort = 7233;
    private static readonly TimeSpan EngineStartDeadline = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan NotReadyDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LostLineDeadline = TimeSpan.FromSeconds(30);

    // Readiness follows the engine's answer on the current connection, which may come back
    // before the host has replaced that connection; the host logs the recovery when its own
    // reconnection succeeds, at most one 60 s backoff step plus a connection later.
    private static readonly TimeSpan RecoveredLineDeadline = TimeSpan.FromSeconds(90);

    private readonly PinnedImage _image = CompositionPins.Load().WorkflowEngine;
    private readonly int _hostPort = FreePort();
    private readonly LogCapture _logs = new();
    private IContainer? _engine;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }

        _logs.Dispose();
    }

    [Fact]
    public async Task WorkerIsReadyOnlyWhileTheEngineAnswersAndRecoversWithoutARestart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await StartEngineAsync(cancellationToken);
        await using var worker = Worker();
        using var client = worker.CreateClient();
        var host = worker.Services.GetRequiredService<TemporalWorkerHost>();

        await WaitForStatusAsync(client, HttpStatusCode.OK, ReadyDeadline, cancellationToken);
        await AssertOnePollerAsync("report-orchestration", TaskQueueType.Workflow, cancellationToken);
        await AssertOnePollerAsync("report-data", TaskQueueType.Activity, cancellationToken);

        await StopEngineAsync();
        await WaitForStatusAsync(client, HttpStatusCode.ServiceUnavailable, NotReadyDeadline, cancellationToken);
        await WaitForLineAsync("engine connection lost", LostLineDeadline, cancellationToken);

        await StartEngineAsync(cancellationToken);
        await WaitForStatusAsync(client, HttpStatusCode.OK, ReadyDeadline, cancellationToken);
        await WaitForLineAsync("engine connection recovered", RecoveredLineDeadline, cancellationToken);

        Assert.Same(host, worker.Services.GetRequiredService<TemporalWorkerHost>());
        Assert.Single(Lines("engine connection lost"));
        Assert.Single(Lines("engine connection recovered"));
        Assert.Single(Lines("starting orchestration-worker"));
    }

    [Fact]
    public async Task WorkerStartedWithoutAnEngineStaysAliveAndBecomesReadyWhenItAppears()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var worker = Worker();
        using var client = worker.CreateClient();

        await WaitForLineAsync("could not connect to the workflow engine", LostLineDeadline, cancellationToken);
        using (var response = await client.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        await StartEngineAsync(cancellationToken);
        await WaitForStatusAsync(client, HttpStatusCode.OK, ReadyDeadline, cancellationToken);

        Assert.Single(Lines("connected to the workflow engine"));
    }

    private WebApplicationFactory<OrchestrationWorker> Worker() =>
        new WebApplicationFactory<OrchestrationWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_hostPort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, MetadataDatabaseStub.ConnectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MetadataDatabaseStub.ExpectedVersion);
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
            builder.ConfigureTestServices(services => MetadataDatabaseStub.UseHealthy(services));
        });

    private async Task StartEngineAsync(CancellationToken cancellationToken)
    {
        _engine = ContainerSmoke.Builder(_image, "temporal-readiness")
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0")
            .WithPortBinding(_hostPort, GrpcPort)
            .Build();
        await ContainerSmoke.StartAsync(_engine, _image, cancellationToken);
        await Wait.UntilAsync(
            $"Temporal gRPC ({_image.Reference}) on port {_hostPort}",
            async token =>
            {
                var engine = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions($"127.0.0.1:{_hostPort}"));
                return await engine.Connection.CheckHealthAsync(options: new RpcOptions { CancellationToken = token });
            },
            EngineStartDeadline,
            TimeSpan.FromSeconds(1),
            cancellationToken);
    }

    private async Task StopEngineAsync()
    {
        await _engine!.DisposeAsync();
        _engine = null;
    }

    /// <summary>
    /// States the queue's promise: its engine worker polls it, and
    /// <c>DescribeTaskQueue</c> reports exactly one poller. Readiness answers as soon
    /// as the connection is made, a moment before the workers are created, so the
    /// poller is awaited within the same deadline the rest of the case uses — and
    /// then asserted, never merely awaited.
    /// </summary>
    private async Task AssertOnePollerAsync(string queue, TaskQueueType type, CancellationToken cancellationToken)
    {
        var engine = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions($"127.0.0.1:{_hostPort}"));
        var pollers = new List<PollerInfo>();
        await Wait.UntilAsync(
            $"a poller on '{queue}'",
            async token =>
            {
                var description = await engine.WorkflowService.DescribeTaskQueueAsync(
                    new DescribeTaskQueueRequest { Namespace = "default", TaskQueue = new TaskQueue { Name = queue }, TaskQueueType = type },
                    new RpcOptions { CancellationToken = token });
                pollers.Clear();
                pollers.AddRange(description.Pollers);
                return pollers.Count > 0;
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

        Assert.Single(pollers);
    }

    private static Task WaitForStatusAsync(HttpClient client, HttpStatusCode expected, TimeSpan deadline, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            $"/health answering {(int)expected}",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == expected;
            },
            deadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

    private Task WaitForLineAsync(string fragment, TimeSpan deadline, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            $"the log line '{fragment}'",
            _ => Task.FromResult(Lines(fragment).Count > 0),
            deadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    private List<string> Lines(string fragment) =>
        [.. _logs.Lines.Where(l => l.Contains(fragment, StringComparison.Ordinal))];

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
