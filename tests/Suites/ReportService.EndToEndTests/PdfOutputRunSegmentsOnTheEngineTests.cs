using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using ReportService.Jobs;
using ReportService.Testing;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Client;
using Temporalio.Worker;

namespace ReportService.EndToEndTests;

/// <summary>
/// The PDF output workflow's run segments on the real engine. The workflow under test is the
/// product's, unchanged, on the workflow engine image the composition pins; only its steps
/// are stand-ins that answer at once, so an output of more blocks than one run may complete
/// is cheap to drive. Each run but the last schedules exactly its share of blocks, drains, and
/// continues as a new run carrying only the next index; every run's history stays inside the
/// event bound; and every block is scheduled exactly once across the runs.
/// </summary>
public sealed class PdfOutputRunSegmentsOnTheEngineTests
{
    /// <summary>The blocks the stand-in partition reports: more than one run's share.</summary>
    public const int BlockCount = 1_200;

    /// <summary>The most events one run of any workflow may hold.</summary>
    public const int HistoryBound = 10_000;

    private const int EnginePort = 7233;

    private const string EngineNamespace = "default";

    private static readonly TimeSpan EngineReadyDeadline = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan OutputDeadline = TimeSpan.FromMinutes(10);

    public static TheoryData<int> Windows { get; } = new(2, 64);

    [Theory]
    [MemberData(nameof(Windows))]
    public async Task EachRunCompletesItsShareInsideTheBoundAndEveryBlockOnce(int window)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var engine = new ContainerBuilder(EngineImage())
            .WithName($"report-service-run-segments-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]}")
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0")
            .WithPortBinding(EnginePort, true)
            .Build();
        await engine.StartAsync(cancellationToken);
        var client = await ConnectAsync(engine, cancellationToken);

        var steps = new StandInSteps(BlockCount);
        var previousWindow = ReportPdfOutputWorkflow.CurrentWindow();
        ReportPdfOutputWorkflow.Configure(window);
        try
        {
            using var orchestration = new TemporalWorker(
                client,
                new TemporalWorkerOptions(ReportJobActivityOptions.Queue)
                    .AddWorkflow<ReportPdfOutputWorkflow>()
                    .AddAllActivities(steps));
            using var rendering = new TemporalWorker(
                client,
                new TemporalWorkerOptions(RenderPdfBlockStepOptions.Queue).AddAllActivities(steps));
            using var merge = new TemporalWorker(
                client,
                new TemporalWorkerOptions(AssemblePdfStepOptions.Queue).AddAllActivities(steps));

            var stopwatch = Stopwatch.StartNew();
            var workflowId = $"report-job/run-segments-{window}-{Guid.NewGuid():N}/output/pdf";
            var handle = await ExecuteWithWorkersAsync(
                [orchestration, rendering, merge],
                async () =>
                {
                    var started = await client.StartWorkflowAsync(
                        (ReportPdfOutputWorkflow workflow) => workflow.RunAsync(
                            new ReportPdfOutputInput("run-segments-job", "run-segments-output")),
                        new WorkflowOptions(workflowId, ReportJobActivityOptions.Queue));
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(OutputDeadline);
                    await started.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = deadline.Token });
                    return started;
                },
                cancellationToken);
            stopwatch.Stop();

            var runs = await ReadRunChainAsync(client, workflowId, handle.FirstExecutionRunId ?? handle.ResultRunId!, cancellationToken);

            var expectedRuns = (BlockCount + PdfOutputWindow.ContinueAsNewAfter - 1) / PdfOutputWindow.ContinueAsNewAfter;
            Assert.Equal(expectedRuns, runs.Count);
            var scheduled = new List<int>();
            for (var run = 0; run < runs.Count; run++)
            {
                var events = runs[run];
                var last = run == runs.Count - 1;
                var renders = RenderIndices(events);

                Assert.True(events.Count <= HistoryBound, $"run {run + 1} holds {events.Count} events, over the bound of {HistoryBound}.");
                Assert.Contains(PatchIds(events), patch => patch.Contains(ReportPdfOutputWorkflow.ContinueAsNewDrainPatch, StringComparison.Ordinal));
                Assert.Equal(
                    last ? BlockCount - (run * PdfOutputWindow.ContinueAsNewAfter) : PdfOutputWindow.ContinueAsNewAfter,
                    renders.Count);
                Assert.Equal(
                    last ? EventType.WorkflowExecutionCompleted : EventType.WorkflowExecutionContinuedAsNew,
                    events[^1].EventType);
                scheduled.AddRange(renders);

                var perBlock = renders.Count == 0 ? 0 : (double)(events.Count - Overhead(events)) / renders.Count;
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"window={window} run={run + 1} events={events.Count} blocks={renders.Count} eventsPerBlock={perBlock.ToString("F2", CultureInfo.InvariantCulture)}");
            }

            Assert.Equal(Enumerable.Range(0, BlockCount), scheduled.Order());
            Assert.Equal(Enumerable.Range(0, BlockCount), steps.Rendered.Order());
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"window={window} runs={runs.Count} elapsed={stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s");
        }
        finally
        {
            ReportPdfOutputWorkflow.Configure(previousWindow);
        }
    }

    /// <summary>The workflow engine image and tag the composition pins, read from its pin file.</summary>
    /// <returns>The image reference.</returns>
    internal static string EngineImage()
    {
        var path = Path.Combine(RepositoryPaths.Root, "src", "ReportService.AppHost", "composition-pins.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var engine = document.RootElement.GetProperty("workflowEngine");
        return $"{engine.GetProperty("image").GetString()}:{engine.GetProperty("tag").GetString()}";
    }

    private static async Task<TemporalClient> ConnectAsync(IContainer engine, CancellationToken cancellationToken)
    {
        var address = $"{engine.Hostname}:{engine.GetMappedPublicPort(EnginePort)}";
        var deadline = DateTimeOffset.UtcNow + EngineReadyDeadline;
        while (true)
        {
            try
            {
                var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(address) { Namespace = EngineNamespace });
                await client.Connection.CheckHealthAsync();
                await client.WorkflowService.GetSystemInfoAsync(new Temporalio.Api.WorkflowService.V1.GetSystemInfoRequest());
                return client;
            }
            catch (Exception exception) when (exception is not OperationCanceledException && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private static async Task<T> ExecuteWithWorkersAsync<T>(
        IReadOnlyList<TemporalWorker> workers,
        Func<Task<T>> body,
        CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = workers.Select(worker => worker.ExecuteAsync(stop.Token)).ToList();
        try
        {
            return await body();
        }
        finally
        {
            await stop.CancelAsync();
            foreach (var task in running)
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                    // The workers stop when the case is done.
                }
            }
        }
    }

    /// <summary>Every run of the output, in order, following each continuation to the next run.</summary>
    private static async Task<IReadOnlyList<IReadOnlyList<HistoryEvent>>> ReadRunChainAsync(
        TemporalClient client,
        string workflowId,
        string firstRunId,
        CancellationToken cancellationToken)
    {
        var runs = new List<IReadOnlyList<HistoryEvent>>();
        var runId = firstRunId;
        while (runId is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var history = await client.GetWorkflowHandle(workflowId, runId).FetchHistoryAsync();
            var events = history.Events.ToList();
            runs.Add(events);
            runId = events[^1].EventType == EventType.WorkflowExecutionContinuedAsNew
                ? events[^1].WorkflowExecutionContinuedAsNewEventAttributes.NewExecutionRunId
                : null;
        }

        return runs;
    }

    /// <summary>The block index of every render a run scheduled, read from the scheduled input.</summary>
    private static List<int> RenderIndices(IReadOnlyList<HistoryEvent> events) =>
        events
            .Where(historyEvent => historyEvent.EventType == EventType.ActivityTaskScheduled
                && historyEvent.ActivityTaskScheduledEventAttributes.ActivityType.Name == PdfOutputStepContracts.RenderPdfBlock)
            .Select(historyEvent =>
            {
                using var input = JsonDocument.Parse(historyEvent.ActivityTaskScheduledEventAttributes.Input.Payloads_[0].Data.ToStringUtf8());
                return input.RootElement.GetProperty(nameof(RenderPdfBlockStepInput.BlockIndex)).GetInt32();
            })
            .ToList();

    /// <summary>The patch identifiers a run recorded.</summary>
    private static List<string> PatchIds(IReadOnlyList<HistoryEvent> events) =>
        events
            .Where(historyEvent => historyEvent.EventType == EventType.MarkerRecorded)
            .Select(historyEvent => historyEvent.MarkerRecordedEventAttributes.Details["patch-data"].Payloads_[0].Data.ToStringUtf8())
            .ToList();

    /// <summary>The events of a run outside its block renders: before the first render is scheduled and after the last completes.</summary>
    private static int Overhead(IReadOnlyList<HistoryEvent> events)
    {
        var renderScheduled = events
            .Where(historyEvent => historyEvent.EventType == EventType.ActivityTaskScheduled
                && historyEvent.ActivityTaskScheduledEventAttributes.ActivityType.Name == PdfOutputStepContracts.RenderPdfBlock)
            .Select(historyEvent => historyEvent.EventId)
            .ToHashSet();
        if (renderScheduled.Count == 0)
        {
            return events.Count;
        }

        var firstRender = events.TakeWhile(historyEvent => !renderScheduled.Contains(historyEvent.EventId)).Count();
        var lastCompletion = events
            .Select((historyEvent, position) => (historyEvent, position))
            .Last(entry => entry.historyEvent.EventType == EventType.ActivityTaskCompleted
                && renderScheduled.Contains(entry.historyEvent.ActivityTaskCompletedEventAttributes.ScheduledEventId))
            .position;
        return firstRender + (events.Count - 1 - lastCompletion);
    }

    /// <summary>The output's steps, answering at once: the partition reports its blocks, each render records its index.</summary>
    private sealed class StandInSteps(int blockCount)
    {
        /// <summary>Every block index a render was asked for, across all runs.</summary>
        public ConcurrentBag<int> Rendered { get; } = [];

        [Activity(JobTransitionActivities.TransitionOutputName)]
        public Task<TransitionOutcome> TransitionOutputAsync(TransitionOutputInput input) =>
            Task.FromResult(TransitionOutcome.Applied);

        [Activity(PdfOutputPartitionFacts.Name)]
        public Task<PdfOutputPartitionFactsResult> ReadPartitionFactsAsync(PdfOutputPartitionFactsInput input) =>
            Task.FromResult(new PdfOutputPartitionFactsResult(blockCount, blockCount));

        [Activity(PdfOutputStepContracts.RenderPdfBlock)]
        public Task<RenderPdfBlockStepResult> RenderAsync(RenderPdfBlockStepInput input)
        {
            Rendered.Add(input.BlockIndex);
            return Task.FromResult(new RenderPdfBlockStepResult($"parts/stand-in/{input.BlockIndex}", new string('0', 64), 1, 1));
        }

        [Activity(AssemblePdfStepContracts.AssemblePdf)]
        public Task<AssemblePdfStepResult> AssembleAsync(AssemblePdfStepInput input) =>
            Task.FromResult(new AssemblePdfStepResult("outputs/stand-in", new string('0', 64), 1, blockCount));

        [Activity(PdfOutputActivities.PublishOutputName)]
        public Task<PublishOutputResult> PublishOutputAsync(PublishOutputInput input) =>
            Task.FromResult(new PublishOutputResult(PublishOutputResult.Published, "artifact-stand-in"));
    }
}
