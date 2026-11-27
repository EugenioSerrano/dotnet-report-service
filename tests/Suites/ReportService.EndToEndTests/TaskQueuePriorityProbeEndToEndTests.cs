using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using ReportService.Testing;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace ReportService.EndToEndTests;

/// <summary>
/// The engine facts the per-class dispatch rests on, measured on the engine the composition
/// pins, with throw-away workers hosted in the test process on task queues of their own (no
/// product worker and no product queue is involved): the server the pin runs; dedicated
/// per-class queues keeping the order; a pending count per queue that matches a known backlog;
/// and a workflow's own activities told apart as scheduled or started. Every case writes its
/// raw figures to the output and to the run's evidence, and stays in the suite as a regression
/// case of these facts.
/// </summary>
public sealed class TaskQueuePriorityProbeEndToEndTests(TaskQueuePriorityProbeEndToEndTests.Engine engine)
    : IClassFixture<TaskQueuePriorityProbeEndToEndTests.Engine>
{
    private const int Trials = 20;

    private static readonly TimeSpan LoadedActivity = TimeSpan.FromSeconds(5);


    [Fact]
    public async Task M0TheServerThePinRunsIsRecorded()
    {
        var token = TestContext.Current.CancellationToken;
        var info = await engine.Client.WorkflowService.GetSystemInfoAsync(new GetSystemInfoRequest(), new RpcOptions { CancellationToken = token });
        var text = $"server_version={info.ServerVersion}{Environment.NewLine}capabilities={info.Capabilities}{Environment.NewLine}";
        Report("m0-server.txt", text);
        Assert.False(string.IsNullOrWhiteSpace(info.ServerVersion), "the server answered no version");
    }

    [Fact]
    public async Task M1DedicatedClassQueuesCarryTheOrder()
    {
        var token = TestContext.Current.CancellationToken;
        var lines = new StringBuilder();
        var passed = 0;
        var misrouted = 0;

        var trials = Enumerable.Range(0, Trials).Select(async trial =>
        {
            var probe = new Probe($"m1-{trial}");
            var queues = probe.ClassQueues();
            using var workers = new WorkerSet();
            foreach (var queue in queues)
            {
                workers.Start(engine.Client, new TemporalWorkerOptions(queue) { MaxConcurrentActivities = 1 }.AddAllActivities(probe.Activities), token);
            }

            for (var i = 0; i < 6; i++)
            {
                await engine.ScheduleAsync(probe, queues[2], $"low-{i}", LoadedActivity, token);
            }

            var firstLow = await probe.WaitForStartAsync(r => r.Queue == queues[2], token);
            await engine.ScheduleAsync(probe, queues[0], "high", LoadedActivity, token);
            var high = await probe.WaitForStartAsync(r => r.Name == "high", token);

            var ok = high.Started < firstLow.Started + LoadedActivity;
            var wrong = probe.Records.Count(r => !string.Equals(r.Queue, r.ScheduledOn, StringComparison.Ordinal));
            lock (lines)
            {
                passed += ok ? 1 : 0;
                misrouted += wrong;
                lines.AppendLine(CultureInfo.InvariantCulture, $"trial={trial} high_started_after_first_low_ms={(high.Started - firstLow.Started).TotalMilliseconds:0} ok={ok} misrouted={wrong}");
            }

            await engine.TerminateAsync(probe, token);
        });
        await Task.WhenAll(trials);

        lines.AppendLine(CultureInfo.InvariantCulture, $"passed={passed}/{Trials} misrouted={misrouted}");
        Report("m1-dedicated.txt", lines.ToString());
        Assert.Equal(Trials, passed);
        Assert.Equal(0, misrouted);
    }

    [Fact]
    public async Task M3APendingCountPerQueueTracksAKnownBacklog()
    {
        var token = TestContext.Current.CancellationToken;
        var probe = new Probe("m3");
        var queue = probe.ClassQueues()[1];
        for (var i = 0; i < 20; i++)
        {
            await engine.ScheduleAsync(probe, queue, $"pending-{i}", TimeSpan.FromMilliseconds(10), token);
        }

        var lines = new StringBuilder();
        var stats = await ReadUntilAsync(queue, 20, PendingSource.Stats, lines, token);
        var hint = await ReadUntilAsync(queue, 20, PendingSource.PartitionHints, lines, token);

        using (var workers = new WorkerSet())
        {
            workers.Start(engine.Client, new TemporalWorkerOptions(queue) { MaxConcurrentActivities = 5 }.AddAllActivities(probe.Activities), token);
            await Wait.UntilAsync("the backlog drained", _ => Task.FromResult(probe.Records.Count >= 20), TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(250), token);
        }

        var statsDrained = stats && await ReadUntilAsync(queue, 0, PendingSource.Stats, lines, token);
        var hintDrained = hint && await ReadUntilAsync(queue, 0, PendingSource.PartitionHints, lines, token);
        var chosen = statsDrained ? "stats" : hintDrained ? "partition-hints" : "none";
        lines.AppendLine(CultureInfo.InvariantCulture, $"stats_tracks={statsDrained} partition_hints_track={hintDrained} source={chosen}");
        Report("m3-pending-count.txt", lines.ToString());
        await engine.TerminateAsync(probe, token);
        Assert.NotEqual("none", chosen);
    }

    [Fact]
    public async Task M4AWorkflowsOwnActivitiesAreToldScheduledFromStarted()
    {
        var token = TestContext.Current.CancellationToken;
        var probe = new Probe("m4");
        var queue = probe.ClassQueues()[1];
        using var workers = new WorkerSet();
        workers.Start(engine.Client, new TemporalWorkerOptions(queue) { MaxConcurrentActivities = 1 }.AddAllActivities(probe.Activities), token);

        var handle = await engine.Client.StartWorkflowAsync(
            (ProbeTwoWorkflow wf) => wf.RunAsync(new ProbeStep(queue, "pair", 10_000)),
            new WorkflowOptions($"{probe.Prefix}-pair", engine.WorkflowQueue));
        var first = await probe.WaitForStartAsync(_ => true, token);

        var lines = new StringBuilder();
        var correct = 0;
        for (var read = 0; read < Trials; read++)
        {
            var description = await engine.Client.WorkflowService.DescribeWorkflowExecutionAsync(
                new DescribeWorkflowExecutionRequest
                {
                    Namespace = Engine.Namespace,
                    Execution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = handle.Id },
                },
                new RpcOptions { CancellationToken = token });
            var states = description.PendingActivities.Select(a => a.State).ToList();
            var truthStarted = probe.Records.Count;
            var reportedStarted = states.Count(s => s == PendingActivityState.Started);
            var reportedScheduled = states.Count(s => s == PendingActivityState.Scheduled);
            var ok = reportedStarted == truthStarted && reportedScheduled == 2 - truthStarted;
            correct += ok ? 1 : 0;
            lines.AppendLine(CultureInfo.InvariantCulture, $"read={read} started_truth={truthStarted} reported_started={reportedStarted} reported_scheduled={reportedScheduled} ok={ok}");
            await Task.Delay(200, token);
        }

        lines.AppendLine(CultureInfo.InvariantCulture, $"first_started_s_after_schedule={(first.Started - first.Scheduled).TotalSeconds:0.000} correct={correct}/{Trials}");
        Report("m4-pending-state.txt", lines.ToString());
        await handle.TerminateAsync(options: new WorkflowTerminateOptions { Rpc = new RpcOptions { CancellationToken = token } });
        Assert.Equal(Trials, correct);
    }

    private enum PendingSource
    {
        Stats,
        PartitionHints,
    }

    private async Task<bool> ReadUntilAsync(string queue, long expected, PendingSource source, StringBuilder lines, CancellationToken token)
    {
        long? last = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                last = source == PendingSource.Stats
                    ? await ReadStatsAsync(queue, token)
                    : await ReadPartitionHintsAsync(queue, token);
            }
            catch (RpcException failure)
            {
                lines.AppendLine(CultureInfo.InvariantCulture, $"source={source} expected={expected} error={failure.Code}: {failure.Message}");
                return false;
            }

            if (last == expected)
            {
                lines.AppendLine(CultureInfo.InvariantCulture, $"source={source} expected={expected} read={last}");
                return true;
            }

            await Task.Delay(250, token);
        }

        lines.AppendLine(CultureInfo.InvariantCulture, $"source={source} expected={expected} last_read={last?.ToString(CultureInfo.InvariantCulture) ?? "none"} (not reached in 10 s)");
        return false;
    }

    private Task<long?> ReadStatsAsync(string queue, CancellationToken token) =>
        Engine.ReadBacklogAsync(engine.Client, queue, token);

    // The legacy per-partition status is the fallback source the plan names; it is obsolete in
    // the API on purpose, and this measurement is what decides whether it is still usable.
#pragma warning disable CS0612
    private async Task<long?> ReadPartitionHintsAsync(string queue, CancellationToken token)
    {
        // The root partition and the default three others of a queue, each described alone.
        long total = 0;
        var names = new[] { queue }.Concat(Enumerable.Range(1, 3).Select(n => $"/_sys/{queue}/{n}"));
        foreach (var name in names)
        {
            var response = await engine.Client.WorkflowService.DescribeTaskQueueAsync(
                new DescribeTaskQueueRequest
                {
                    Namespace = Engine.Namespace,
                    TaskQueue = new Temporalio.Api.TaskQueue.V1.TaskQueue { Name = name, Kind = TaskQueueKind.Normal },
                    TaskQueueType = TaskQueueType.Activity,
                    IncludeTaskQueueStatus = true,
                },
                new RpcOptions { CancellationToken = token });
            total += response.TaskQueueStatus?.BacklogCountHint ?? 0;
        }

        return total;
    }
#pragma warning restore CS0612

    private static void Report(string fileName, string text)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(text);
        RunEnvironment.WriteEvidence("priority-probe-" + fileName, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>One activity start, as the activity itself saw it.</summary>
    public sealed record StartRecord(string Name, string Queue, string ScheduledOn, DateTimeOffset Scheduled, DateTimeOffset Started);

    /// <summary>What one probe activity is asked to do.</summary>
    public sealed record ProbeStep(string Queue, string Name, int DelayMilliseconds);

    /// <summary>The activities of one measurement, with what they saw.</summary>
    public sealed class ProbeActivities
    {
        private int _running;
        private int _maxRunning;

        /// <summary>Every start, in the order the activities started.</summary>
        public ConcurrentQueue<StartRecord> Records { get; } = new();

        /// <summary>The most activities this probe ever saw running at once.</summary>
        public int MaxRunning => Volatile.Read(ref _maxRunning);

        /// <summary>Records the start, holds the slot for the step's delay, and ends.</summary>
        [Activity("PriorityProbeWork")]
        public async Task<string> WorkAsync(ProbeStep step)
        {
            var info = ActivityExecutionContext.Current.Info;
            var now = Interlocked.Increment(ref _running);
            int seen;
            while ((seen = Volatile.Read(ref _maxRunning)) < now && Interlocked.CompareExchange(ref _maxRunning, now, seen) != seen)
            {
            }

            Records.Enqueue(new StartRecord(step.Name, info.TaskQueue, step.Queue, info.CurrentAttemptScheduledTime, info.StartedTime));
            try
            {
                await Task.Delay(step.DelayMilliseconds, ActivityExecutionContext.Current.CancellationToken);
                return step.Name;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    /// <summary>Runs one probe activity on the queue its step names.</summary>
    [Workflow("PriorityProbeOne")]
    public sealed class ProbeOneWorkflow
    {
        /// <summary>Runs the step.</summary>
        [WorkflowRun]
        public Task<string> RunAsync(ProbeStep step) =>
            Workflow.ExecuteActivityAsync((ProbeActivities a) => a.WorkAsync(step), Options(step.Queue));

        internal static ActivityOptions Options(string queue) => new()
        {
            TaskQueue = queue,
            StartToCloseTimeout = TimeSpan.FromMinutes(5),
            RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
        };
    }

    /// <summary>Runs two probe activities at once on the queue its step names.</summary>
    [Workflow("PriorityProbeTwo")]
    public sealed class ProbeTwoWorkflow
    {
        /// <summary>Runs both.</summary>
        [WorkflowRun]
        public async Task RunAsync(ProbeStep step)
        {
            var options = ProbeOneWorkflow.Options(step.Queue);
            var first = step with { Name = step.Name + "-a" };
            var second = step with { Name = step.Name + "-b" };
            await Workflow.WhenAllAsync(
                Workflow.ExecuteActivityAsync((ProbeActivities a) => a.WorkAsync(first), options),
                Workflow.ExecuteActivityAsync((ProbeActivities a) => a.WorkAsync(second), options));
        }
    }

    /// <summary>One measurement: its queues, its activities and what they saw.</summary>
    internal sealed class Probe(string name)
    {
        public string Prefix { get; } = $"priority-probe-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]}-{name}";

        public ProbeActivities Activities { get; } = new();

        public IReadOnlyCollection<StartRecord> Records => Activities.Records;

        public int MaxConcurrency => Activities.MaxRunning;

        public List<string> Started { get; } = [];

        /// <summary>The three class queues, highest class first.</summary>
        public string[] ClassQueues() => [$"{Prefix}-high", $"{Prefix}-normal", $"{Prefix}-low"];

        public async Task<StartRecord> WaitForStartAsync(Func<StartRecord, bool> match, CancellationToken token, TimeSpan? within = null)
        {
            StartRecord? found = null;
            await Wait.UntilAsync(
                $"a start in {Prefix}",
                _ =>
                {
                    found = Records.FirstOrDefault(match);
                    return Task.FromResult(found is not null);
                },
                within ?? TimeSpan.FromMinutes(2),
                TimeSpan.FromMilliseconds(50),
                token);
            return found!;
        }
    }

    /// <summary>Workers hosted in the test, stopped together.</summary>
    internal sealed class WorkerSet : IDisposable
    {
        private readonly List<(TemporalWorker Worker, Task Run, CancellationTokenSource Stop)> _workers = [];
        private readonly List<IDisposable> _owned = [];

        /// <summary>Disposes <paramref name="owned"/> after the workers stopped.</summary>
        public void Own(IDisposable owned) => _owned.Add(owned);

        public void Start(ITemporalClient client, TemporalWorkerOptions options, CancellationToken token)
        {
            var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var worker = new TemporalWorker(client, options);
            _workers.Add((worker, worker.ExecuteAsync(stop.Token), stop));
        }

        public void Dispose()
        {
            foreach (var (_, _, stop) in _workers)
            {
                stop.Cancel();
            }

            foreach (var (worker, run, stop) in _workers)
            {
                try
                {
                    run.Wait(TimeSpan.FromSeconds(30));
                }
                catch (AggregateException)
                {
                    // A stopped worker ends with its cancellation; that is the stop asked for.
                }

                worker.Dispose();
                stop.Dispose();
            }

            foreach (var owned in _owned)
            {
                owned.Dispose();
            }
        }
    }

    /// <summary>The pinned engine of the composition, shared by every measurement of the class.</summary>
    public sealed class Engine : IAsyncLifetime
    {
        /// <summary>The namespace the composition's processes use.</summary>
        public const string Namespace = "default";

        private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

        private DistributedApplication? _app;
        private TemporalWorker? _workflowWorker;
        private CancellationTokenSource? _stop;
        private Task? _workflowRun;

        /// <summary>A client of the composition's engine.</summary>
        public ITemporalClient Client { get; private set; } = null!;

        /// <summary>The queue the probe workflows run on.</summary>
        public string WorkflowQueue { get; } = $"priority-probe-workflows-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]}";

        /// <inheritdoc />
        public async ValueTask InitializeAsync()
        {
            var token = TestContext.Current.CancellationToken;
            var builder = await TestComposition.CreateAsync(token);
            _app = await builder.BuildAsync(token);
            await _app.StartAsync(token);
            var endpoint = _app.GetEndpoint("temporal", "grpc");
            var target = $"{endpoint.Host}:{endpoint.Port}";

            await Wait.UntilAsync(
                "the engine answering",
                async t =>
                {
                    try
                    {
                        Client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(target) { Namespace = Namespace });
                        _ = await Client.WorkflowService.GetSystemInfoAsync(new GetSystemInfoRequest(), new RpcOptions { CancellationToken = t });
                        return true;
                    }
                    catch (Exception failure) when (failure is RpcException or InvalidOperationException)
                    {
                        return false;
                    }
                },
                HealthyDeadline,
                TimeSpan.FromSeconds(1),
                token);

            _stop = new CancellationTokenSource();
            _workflowWorker = new TemporalWorker(
                Client,
                new TemporalWorkerOptions(WorkflowQueue).AddWorkflow<ProbeOneWorkflow>().AddWorkflow<ProbeTwoWorkflow>());
            _workflowRun = _workflowWorker.ExecuteAsync(_stop.Token);
        }

        /// <summary>The waiting work of <paramref name="queue"/>, by the source M3 validated.</summary>
        public static async Task<long?> ReadBacklogAsync(ITemporalClient client, string queue, CancellationToken token)
        {
            var response = await client.WorkflowService.DescribeTaskQueueAsync(
                new DescribeTaskQueueRequest
                {
                    Namespace = Namespace,
                    TaskQueue = new Temporalio.Api.TaskQueue.V1.TaskQueue { Name = queue },
                    TaskQueueType = TaskQueueType.Activity,
                    ReportStats = true,
                },
                new RpcOptions { CancellationToken = token });
            return response.Stats?.ApproximateBacklogCount;
        }

        /// <summary>Schedules one probe activity of <paramref name="delay"/> on <paramref name="queue"/>.</summary>
        internal async Task ScheduleAsync(Probe probe, string queue, string name, TimeSpan delay, CancellationToken token)
        {
            var id = $"{probe.Prefix}-{name}";
            probe.Started.Add(id);
            await Client.StartWorkflowAsync(
                (ProbeOneWorkflow wf) => wf.RunAsync(new ProbeStep(queue, name, (int)delay.TotalMilliseconds)),
                new WorkflowOptions(id, WorkflowQueue) { Rpc = new RpcOptions { CancellationToken = token } });
        }

        /// <summary>Ends every workflow the probe started that is still running.</summary>
        internal async Task TerminateAsync(Probe probe, CancellationToken token)
        {
            foreach (var id in probe.Started)
            {
                try
                {
                    await Client.GetWorkflowHandle(id).TerminateAsync(options: new WorkflowTerminateOptions { Rpc = new RpcOptions { CancellationToken = token } });
                }
                catch (RpcException)
                {
                    // Already completed: nothing to end.
                }
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_stop is not null)
            {
                await _stop.CancelAsync();
                try
                {
                    await _workflowRun!;
                }
                catch (OperationCanceledException)
                {
                    // The stop asked for.
                }

                _workflowWorker?.Dispose();
                _stop.Dispose();
            }

            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
        }
    }
}
