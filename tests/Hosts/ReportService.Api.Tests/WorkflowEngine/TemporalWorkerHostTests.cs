using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Testing;

namespace ReportService.Api.Tests.WorkflowEngine;

/// <summary>
/// The worker host connects before anything else, retries with a bounded
/// backoff without exiting, reconnects and replaces a lost connection, and
/// creates engine workers only for queues with registered work.
/// </summary>
public sealed class TemporalWorkerHostTests : IDisposable
{
    private const string PlantedSecret = "planted-secret-91d0aa";
    private static readonly string[] Declared = ["report-orchestration", "report-data"];

    private readonly LogCapture _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly CancellationTokenSource _run = new();
    private readonly TemporalClientHolder _holder = new();
    private readonly EngineWorkerStates _states = new(Declared);

    public TemporalWorkerHostTests() =>
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _run.Dispose();
    }

    [Fact]
    public async Task FailedConnectionsAreRetriedWithDoublingWaitsUntilOneSucceeds()
    {
        var connection = new FakeConnection("first");
        var connector = new ScriptedConnector(
            ScriptedConnector.Refused(), ScriptedConnector.Refused(), ScriptedConnector.Refused(),
            ScriptedConnector.Returns(connection));
        var heldDuringRetries = new List<IEngineConnection?>();
        var delay = new RecordingDelay(_run, stopAfter: 4, onDelay: call =>
        {
            if (call <= 3)
            {
                heldDuringRetries.Add(_holder.Current);
            }
        });

        await RunUntilStoppedAsync(Host(connector, delay));

        Assert.Equal([2, 4, 8], delay.Waits.Take(3).Select(w => w.TotalSeconds));
        Assert.Equal(TemporalWorkerHost.ProbeInterval, delay.Waits[3]);
        Assert.Equal([null, null, null], heldDuringRetries);
        Assert.Same(connection, _holder.Current);
        Assert.Equal(3, Lines("could not connect").Count);
        Assert.Single(Lines("connected to the workflow engine at localhost:7233"));
    }

    [Fact]
    public async Task FailureLinesNameTheAddressAndTheReasonButNoPlantedValue()
    {
        var connector = new ScriptedConnector(ScriptedConnector.Refused("Connection failed: refused"), ScriptedConnector.Returns(new FakeConnection("c")));
        var delay = new RecordingDelay(_run, stopAfter: 2);

        await RunUntilStoppedAsync(Host(connector, delay));

        var line = Assert.Single(Lines("could not connect"));
        Assert.Contains("localhost:7233", line, StringComparison.Ordinal);
        Assert.Contains("Connection failed: refused", line, StringComparison.Ordinal);
        Assert.DoesNotContain(_logs.Lines, l => l.Contains(PlantedSecret, StringComparison.Ordinal));
    }

    [Fact]
    public void WaitsDoubleFromTwoSecondsAndNeverExceedOneMinute()
    {
        var waits = Enumerable.Range(1, 10).Select(TemporalWorkerHost.BackoffDelay).Select(w => w.TotalSeconds);

        Assert.Equal([2, 4, 8, 16, 32, 60, 60, 60, 60, 60], waits);
    }

    [Fact]
    public async Task EachDeclaredQueueWithoutWorkIsLoggedOnceAndNoWorkerIsCreated()
    {
        var created = 0;
        var connector = new ScriptedConnector(ScriptedConnector.Returns(new FakeConnection("c")));
        var delay = new RecordingDelay(_run, stopAfter: 1);

        await RunUntilStoppedAsync(Host(connector, delay, factory: (_, plan) => { created++; return new FakeWorker(plan); }));

        Assert.Equal(0, created);
        Assert.Single(Lines("task queue report-orchestration: no registered work"));
        Assert.Single(Lines("task queue report-data: no registered work"));
        Assert.Equal("no registered work", _states.Snapshot()["report-data"]);
    }

    [Fact]
    public async Task WorkersAreCreatedOnlyAfterTheConnectionOnePerQueueWithWork()
    {
        var connection = new FakeConnection("c");
        var connector = new ScriptedConnector(ScriptedConnector.Refused(), ScriptedConnector.Returns(connection));
        var workers = new List<FakeWorker>();
        var connectorCallsAtCreation = new List<int>();
        var delay = new RecordingDelay(_run, stopAfter: 2);
        var work = new[]
        {
            new TaskQueueWork("report-data", o => o.AddActivity(SampleActivities.First)),
            new TaskQueueWork("report-data", o => o.AddActivity(SampleActivities.Second)),
        };

        await RunUntilStoppedAsync(Host(connector, delay, work, (received, plan) =>
        {
            Assert.Same(connection, received);
            connectorCallsAtCreation.Add(connector.Calls);
            var worker = new FakeWorker(plan);
            workers.Add(worker);
            return worker;
        }));

        var worker = Assert.Single(workers);
        Assert.Equal("report-data", worker.Plan.Queue);
        Assert.Equal([2], connectorCallsAtCreation);
        Assert.Equal("no registered work", _states.Snapshot()["report-orchestration"]);
    }

    [Fact]
    public async Task RunningWorkerIsReportedAndAStoppedOneIsReportedStopped()
    {
        var connector = new ScriptedConnector(ScriptedConnector.Returns(new FakeConnection("c")));
        FakeWorker? worker = null;
        string? stateWhileRunning = null;
        var delay = new RecordingDelay(_run, stopAfter: 3, onDelay: call =>
        {
            if (call == 1)
            {
                stateWhileRunning = _states.Snapshot()["report-data"];
                worker!.Stop();
            }
        });
        var work = new[] { new TaskQueueWork("report-data", o => o.AddActivity(SampleActivities.First)) };

        var host = Host(connector, delay, work, (_, plan) => worker = new FakeWorker(plan));
        await RunUntilStoppedAsync(host);

        Assert.Equal("worker running", stateWhileRunning);
        Assert.Equal("worker stopped", _states.Snapshot()["report-data"]);
        Assert.False(_states.AllRunning);
    }

    [Fact]
    public async Task LostConnectionIsLoggedOnceThenReplacedByANewOneWithoutRestartingWorkers()
    {
        var first = new FakeConnection("first");
        var second = new FakeConnection("second");
        var connector = new ScriptedConnector(
            ScriptedConnector.Returns(first), ScriptedConnector.Refused(), ScriptedConnector.Returns(second));
        FakeWorker? worker = null;
        var delay = new RecordingDelay(_run, stopAfter: 3, onDelay: call =>
        {
            if (call == 1)
            {
                first.Healthy = false;
            }
        });
        var work = new[] { new TaskQueueWork("report-data", o => o.AddActivity(SampleActivities.First)) };
        var created = 0;

        await RunUntilStoppedAsync(Host(connector, delay, work, (_, plan) => { created++; return worker = new FakeWorker(plan); }));

        Assert.Same(second, _holder.Current);
        Assert.True(first.Disposed);
        Assert.Equal(1, created);
        Assert.Equal([second], worker!.ClientsReceived);
        Assert.Equal([TemporalWorkerHost.ProbeInterval.TotalSeconds, 2, TemporalWorkerHost.ProbeInterval.TotalSeconds], delay.Waits.Select(w => w.TotalSeconds));
        Assert.Single(Lines("engine connection lost"));
        Assert.Single(Lines("engine connection recovered"));
    }

    [Fact]
    public async Task HealthyProbesKeepTheSameConnection()
    {
        var connection = new FakeConnection("c");
        var connector = new ScriptedConnector(ScriptedConnector.Returns(connection));
        var delay = new RecordingDelay(_run, stopAfter: 3);

        await RunUntilStoppedAsync(Host(connector, delay));

        Assert.Same(connection, _holder.Current);
        Assert.Equal(1, connector.Calls);
        Assert.Empty(Lines("engine connection lost"));
    }

    [Fact]
    public async Task StoppingDuringABackoffWaitEndsWithCancellationOnly()
    {
        var connector = new ScriptedConnector(ScriptedConnector.Refused(), ScriptedConnector.Refused());
        var delay = new RecordingDelay(_run, stopAfter: 1);

        var exception = await Record.ExceptionAsync(() => Host(connector, delay).RunAsync(_run.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Null(_holder.Current);
    }

    [Fact]
    public async Task StartRefusesWorkOnAnUndeclaredQueueBeforeConnecting()
    {
        var connector = new ScriptedConnector(ScriptedConnector.Returns(new FakeConnection("c")));
        var delay = new RecordingDelay(_run, stopAfter: 1);
        var work = new[] { new TaskQueueWork("report-pdf-rendering", o => o.AddActivity(SampleActivities.First)) };
        using var host = Host(connector, delay, work);

        var error = await Assert.ThrowsAsync<ConfigurationError>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("report-pdf-rendering", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, connector.Calls);
    }

    [Fact]
    public async Task StartedHostConnectsInTheBackgroundAndStopsCleanly()
    {
        var connection = new FakeConnection("c");
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EngineConnector connector = (_, _) => { connected.TrySetResult(); return Task.FromResult<IEngineConnection>(connection); };
        EngineDelay delay = (wait, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        using var host = new TemporalWorkerHost(
            new WorkflowEngineProcess("orchestration-worker", Declared), Options(), _holder, _states, [], connector, delay,
            (_, plan) => new FakeWorker(plan), _loggerFactory.CreateLogger<TemporalWorkerHost>());
        var cancellationToken = TestContext.Current.CancellationToken;

        await host.StartAsync(cancellationToken);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await host.StopAsync(cancellationToken);

        Assert.Single(Lines("starting orchestration-worker"));
    }

    private TemporalWorkerHost Host(
        ScriptedConnector connector,
        RecordingDelay delay,
        IReadOnlyList<TaskQueueWork>? work = null,
        EngineWorkerFactory? factory = null) =>
        new(
            new WorkflowEngineProcess("orchestration-worker", Declared),
            Options(),
            _holder,
            _states,
            work ?? [],
            connector.Connector,
            delay.Delay,
            factory ?? ((_, plan) => new FakeWorker(plan)),
            _loggerFactory.CreateLogger<TemporalWorkerHost>());

    private static IOptions<WorkflowEngineOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new WorkflowEngineOptions { Address = "localhost:7233", Namespace = "default" });

    private async Task RunUntilStoppedAsync(TemporalWorkerHost host)
    {
        using (host)
        {
            var exception = await Record.ExceptionAsync(() => host.RunAsync(_run.Token));
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
        }
    }

    private List<string> Lines(string fragment) =>
        [.. _logs.Lines.Where(l => l.Contains(fragment, StringComparison.Ordinal))];
}
