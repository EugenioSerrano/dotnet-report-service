using System.Globalization;
using ReportService.Testing;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// Records one history in which the engine timed a workflow task out, once, so the
/// hermetic suite can count the events such a timeout writes. It is opt-in: it runs
/// only when the environment variable names the directory to write, so the
/// integration command never runs it, and the ordinary cases of this suite are
/// untouched by it. The workflow it records is defined here, is registered by no
/// worker of the product and exists only to be timed out and then to finish; the
/// recording is taken against the engine version the product pins.
/// </summary>
public sealed class WorkflowTaskTimeoutCapture
{
    /// <summary>The variable that names the directory the captured history is written to; without it the case is skipped.</summary>
    public const string OutputVariable = "REPORTSERVICE_CAPTURE_WORKFLOW_HISTORY";

    /// <summary>The name the captured history is committed under, in the directory the hermetic suite reads.</summary>
    public const string FileName = "report-job-workflow-task-timeout.json";

    /// <summary>
    /// How long one workflow task of the captured execution may take before the engine times it
    /// out. It is the shortest the engine accepts, because the worker holds a task it started for
    /// a while: the timeout has to elapse while the worker still holds it, and the worker reports
    /// a task it has not finished for two seconds as a deadlock instead of letting it run.
    /// </summary>
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromSeconds(1);

    /// <summary>How long a route may take to make the engine record the timeout.</summary>
    private static readonly TimeSpan ArrivalDeadline = TimeSpan.FromSeconds(20);

    /// <summary>How long the execution may take to reach a terminal state once a worker serves it again.</summary>
    private static readonly TimeSpan ExecutionDeadline = TimeSpan.FromMinutes(2);

    /// <summary>How often the recording watches the execution's history.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task TheHistoryOfAWorkflowTaskTheEngineTimedOutIsRecorded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = Target();
        await using var environment = await WorkflowEnvironment.StartLocalAsync(new WorkflowEnvironmentStartLocalOptions());

        var (history, route) = await RecordAsync(environment.Client, cancellationToken);

        // The recording is only worth committing if it carries the event it exists to count, and
        // the assertion is taken before anything is written: no fixture that proves nothing is
        // committed. Exactly one timeout is required, because the figure it measures is the pair
        // one timeout writes — a history with two would leave the count ambiguous.
        var timeouts = history.Events
            .Where(entry => entry.EventType == EventType.WorkflowTaskTimedOut)
            .ToList();
        Assert.True(
            timeouts.Count == 1,
            string.Create(
                CultureInfo.InvariantCulture,
                $"the {route} route recorded {timeouts.Count} workflow-task timeouts (events {string.Join(", ", timeouts.Select(entry => entry.EventId))}) and the recording needs exactly one."));

        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, FileName), history.ToJson(), cancellationToken);
    }

    private static string Target()
    {
        var target = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(target))
        {
            Assert.Skip($"the capture runs once, when {OutputVariable} names the directory to write.");
        }

        return target!;
    }

    /// <summary>
    /// Tries the two routes that can make the engine record a workflow-task timeout, in order, and
    /// returns the first history that carries one together with the route that produced it: an
    /// execution whose task queue no worker serves, then an execution whose worker takes longer
    /// than the task's timeout to complete the task it started.
    /// </summary>
    private static async Task<(WorkflowHistory History, string Route)> RecordAsync(
        ITemporalClient client,
        CancellationToken cancellationToken)
    {
        var unserved = await StartAsync(client, "capture-unserved", ATaskThatOutlivesItsTimeout.UnservedQueueName, cancellationToken);
        if (await WaitForTheTimeoutAsync(client, unserved, ArrivalDeadline, cancellationToken))
        {
            await ServeUntilItFinishesAsync(client, unserved, ATaskThatOutlivesItsTimeout.UnservedQueueName, cancellationToken);
            return (await HistoryAsync(unserved, cancellationToken), "unserved-queue");
        }

        // No worker polled the task inside the window: the engine never started it, so the
        // start-to-close timeout the execution carries could not elapse. That execution is removed
        // and the second route takes a fresh one on its own queue.
        await unserved.TerminateAsync("the unserved queue recorded no workflow-task timeout", new WorkflowTerminateOptions());
        var slow = await StartAsync(client, "capture-slow-task", ATaskThatOutlivesItsTimeout.TaskQueueName, cancellationToken);
        return (await ExceedTheTimeoutAsync(client, slow, cancellationToken), "task-exceeds-its-timeout");
    }

    private static Task<WorkflowHandle<ATaskThatOutlivesItsTimeout>> StartAsync(
        ITemporalClient client,
        string executionId,
        string taskQueue,
        CancellationToken cancellationToken) =>
        client.StartWorkflowAsync(
            (ATaskThatOutlivesItsTimeout workflow) => workflow.RunAsync(),
            new WorkflowOptions
            {
                Id = executionId,
                TaskQueue = taskQueue,
                TaskTimeout = TaskTimeout,
            });

    /// <summary>
    /// Serves the execution's queue with a worker whose first workflow task holds the task past the
    /// timeout the execution was started with, and lets it finish once the engine recorded the
    /// timeout — which is the state the recording is of.
    /// </summary>
    private static async Task<WorkflowHistory> ExceedTheTimeoutAsync(
        ITemporalClient client,
        WorkflowHandle<ATaskThatOutlivesItsTimeout> handle,
        CancellationToken cancellationToken)
    {
        ATaskThatOutlivesItsTimeout.HoldTheFirstTask();
        using var stop = new CancellationTokenSource();
        using var worker = new TemporalWorker(
            client,
            new TemporalWorkerOptions(ATaskThatOutlivesItsTimeout.TaskQueueName)
                .AddWorkflow<ATaskThatOutlivesItsTimeout>());
        var running = worker.ExecuteAsync(stop.Token);
        try
        {
            Assert.True(
                await WaitForTheTimeoutAsync(client, handle, ArrivalDeadline, cancellationToken),
                "the engine did not time the held workflow task out inside the case's window.");

            // Released only now: the task the worker is still holding belongs to an attempt the
            // engine has already timed out, so its late answer changes nothing.
            ATaskThatOutlivesItsTimeout.ReleaseTheTask();
            await handle.GetResultAsync().WaitAsync(ExecutionDeadline, cancellationToken);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        }

        return await HistoryAsync(handle, cancellationToken);
    }

    /// <summary>Serves the execution's queue until it reaches a terminal state.</summary>
    private static async Task ServeUntilItFinishesAsync(
        ITemporalClient client,
        WorkflowHandle<ATaskThatOutlivesItsTimeout> handle,
        string taskQueue,
        CancellationToken cancellationToken)
    {
        using var stop = new CancellationTokenSource();
        using var worker = new TemporalWorker(
            client,
            new TemporalWorkerOptions(taskQueue).AddWorkflow<ATaskThatOutlivesItsTimeout>());
        var running = worker.ExecuteAsync(stop.Token);
        try
        {
            await handle.GetResultAsync().WaitAsync(ExecutionDeadline, cancellationToken);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        }
    }

    private static Task<WorkflowHistory> HistoryAsync(
        WorkflowHandle<ATaskThatOutlivesItsTimeout> handle,
        CancellationToken cancellationToken) =>
        handle.FetchHistoryAsync(new WorkflowHistoryEventFetchOptions()).WaitAsync(ExecutionDeadline, cancellationToken);

    /// <summary>Waits until the execution's history records the timeout, or the window closes.</summary>
    private static async Task<bool> WaitForTheTimeoutAsync(
        ITemporalClient client,
        WorkflowHandle<ATaskThatOutlivesItsTimeout> handle,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        try
        {
            await Wait.UntilAsync(
                $"the engine to record a workflow-task timeout for {handle.Id}",
                async token =>
                {
                    var history = await handle.FetchHistoryAsync(new WorkflowHistoryEventFetchOptions())
                        .WaitAsync(ExecutionDeadline, token);
                    return history.Events.Any(entry => entry.EventType == EventType.WorkflowTaskTimedOut);
                },
                window,
                PollInterval,
                cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            // A route is not promised to work: the case records what it saw and tries the next.
            return false;
        }
    }

    /// <summary>
    /// The workflow the recording is taken of. It is not a product type and no worker of the
    /// product registers it: its whole purpose is to hold its first workflow task past the
    /// timeout it was started with, and then to finish.
    /// </summary>
    [Workflow]
    public sealed class ATaskThatOutlivesItsTimeout
    {
        /// <summary>The queue of the second route, which the capture's own worker serves.</summary>
        public const string TaskQueueName = "capture-workflow-task-timeout";

        /// <summary>The queue of the first route, which no worker serves at all.</summary>
        public const string UnservedQueueName = "capture-workflow-task-timeout-unserved";

        private static readonly ManualResetEventSlim Held = new(false);

        private static volatile bool _holdTheFirstTask;

        /// <summary>Holds the first workflow task of the execution, until the case releases it.</summary>
        public static void HoldTheFirstTask() => _holdTheFirstTask = true;

        /// <summary>Releases a workflow task that is being held.</summary>
        public static void ReleaseTheTask() => Held.Set();

        /// <summary>Runs once, holding the workflow task it was started for when the case asked it to.</summary>
        /// <returns>A task that completes when the run finishes.</returns>
        [WorkflowRun]
        public Task RunAsync()
        {
            if (_holdTheFirstTask)
            {
                _holdTheFirstTask = false;

                // Long enough for the engine to time the task out, and bounded so a case that never
                // saw the timeout still ends: the release that matters is the case's, at the mark.
                Held.Wait(TimeSpan.FromSeconds(30));
            }

            return Task.CompletedTask;
        }
    }
}
