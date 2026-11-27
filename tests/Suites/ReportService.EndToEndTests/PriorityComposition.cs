using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Testing;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;

namespace ReportService.EndToEndTests;

/// <summary>
/// The priority classes: each starts a composition with one render and one merge worker per
/// class, so they run one at a time and never beside each other, to keep the host's memory and
/// the container runtime's load within what one composition needs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PriorityCompositionCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "priority-composition";
}

/// <summary>
/// The composition the priority cases start: the multi-block stage with one render and one merge
/// worker per class (the normal ones are the composition's own), a short aging wait and a short
/// pending-work sample interval. Every other end-to-end class keeps its own composition. The
/// helpers read what the cases assert from the engine's recorded histories, the job's status
/// read and the orchestration worker's telemetry capture - never from timing guesses.
/// </summary>
internal sealed class PriorityComposition : IDisposable
{
    /// <summary>The aging wait the priority composition runs with.</summary>
    public static readonly TimeSpan AgingWait = TimeSpan.FromSeconds(20);

    /// <summary>The render worker resources of the high and low classes.</summary>
    public const string HighRenderWorker = "render-worker-high";

    /// <summary>The render worker resource of the low class.</summary>
    public const string LowRenderWorker = "render-worker-low";

    /// <summary>The merge worker resource of the low class.</summary>
    public const string LowMergeWorker = "merge-worker-low";

    /// <summary>Starts a stage whose composition carries the per-class workers.</summary>
    /// <param name="accounted">Whether the stage prints the page-accounting fixture, whose job holds more blocks than the window.</param>
    /// <param name="extraArguments">Settings a case adds.</param>
    public PriorityComposition(bool accounted = false, params string[] extraArguments)
    {
        Stage = new MultiBlockStage(accounted: accounted, compositionArguments:
        [
            "--Composition:PriorityClassWorkers=true",
            "--Priority:Aging:Wait=" + AgingWait.ToString("c", CultureInfo.InvariantCulture),
            "--Telemetry:PendingWork:SampleInterval=00:00:02",
            .. extraArguments,
        ]);
    }

    /// <summary>The stage the cases drive.</summary>
    public MultiBlockStage Stage { get; }

    /// <summary>A client of the composition's engine.</summary>
    public static async Task<ITemporalClient> EngineAsync(DistributedApplication app)
    {
        var endpoint = app.GetEndpoint("temporal", "grpc");
        return await TemporalClient.ConnectAsync(new TemporalClientConnectOptions($"{endpoint.Host}:{endpoint.Port}") { Namespace = "default" });
    }

    /// <summary>Takes the template, seeds an upload of <paramref name="records"/> records and submits a job at <paramref name="priority"/>; the upload is completed.</summary>
    public async Task<string> SubmitAsync(MultiBlockStage.Composition composition, string priority, int? records, CancellationToken cancellationToken, string outputs = "\"pdf\"")
    {
        var uploadId = await Stage.SeedConfirmingUploadAsync(composition, cancellationToken, records);
        var reportJobId = await Stage.SubmitAsync(composition, uploadId, cancellationToken, outputs, priority);
        await Stage.CompleteUploadAsync(composition, uploadId, cancellationToken);
        return reportJobId;
    }

    /// <summary>The job's declared priority and effective class, as the status read answers them.</summary>
    public static async Task<(string Declared, string Effective)> PriorityOfAsync(MultiBlockStage.Composition composition, string reportJobId, CancellationToken cancellationToken)
    {
        using var api = composition.App.CreateHttpClient("api");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, MultiBlockStage.Consumer);
        using var response = await api.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var priority = document.RootElement.GetProperty("priority");
        return (priority.GetProperty("declared").GetString()!, priority.GetProperty("effective").GetString()!);
    }

    /// <summary>Waits until the job's effective class reads <paramref name="expected"/>.</summary>
    public static Task WaitForEffectiveAsync(MultiBlockStage.Composition composition, string reportJobId, string expected, TimeSpan deadline, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            $"job {reportJobId} effective class {expected}",
            async token => (await PriorityOfAsync(composition, reportJobId, token)).Effective == expected,
            deadline,
            TimeSpan.FromSeconds(1),
            cancellationToken);

    /// <summary>Whether the output's execution currently has render or assembly work scheduled and not started.</summary>
    public static async Task<bool> HasWaitingWorkAsync(ITemporalClient engine, string reportJobId, CancellationToken cancellationToken)
    {
        DescribeWorkflowExecutionResponse description;
        try
        {
            description = await engine.WorkflowService.DescribeWorkflowExecutionAsync(
                new DescribeWorkflowExecutionRequest
                {
                    Namespace = "default",
                    Execution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = ReportPdfOutputWorkflow.WorkflowIdFor(reportJobId) },
                },
                new RpcOptions { CancellationToken = cancellationToken });
        }
        catch (Temporalio.Exceptions.RpcException)
        {
            return false;
        }

        return PendingDispatchMapper.AnyWaiting(description);
    }

    /// <summary>Every activity a workflow execution scheduled, in order, across its runs (continue-as-new included).</summary>
    public static async Task<IReadOnlyList<ScheduledActivity>> ScheduledAsync(ITemporalClient engine, string workflowId, CancellationToken cancellationToken)
    {
        var scheduled = new List<ScheduledActivity>();
        var runId = string.Empty;
        string? next = null;
        var visited = new HashSet<string>(StringComparer.Ordinal);

        // The latest run first, then each predecessor its first event names.
        var handle = engine.GetWorkflowHandle(workflowId);
        while (true)
        {
            var history = await (runId.Length == 0 ? handle : engine.GetWorkflowHandle(workflowId, runId))
                .FetchHistoryAsync(new WorkflowHistoryEventFetchOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });
            var runScheduled = new List<ScheduledActivity>();
            var byScheduledId = new Dictionary<long, int>();
            foreach (var @event in history.Events)
            {
                switch (@event.EventType)
                {
                    case EventType.ActivityTaskScheduled:
                        var attributes = @event.ActivityTaskScheduledEventAttributes;
                        byScheduledId[@event.EventId] = runScheduled.Count;
                        runScheduled.Add(new ScheduledActivity(attributes.ActivityType.Name, attributes.TaskQueue.Name, @event.EventTime.ToDateTimeOffset(), null, null, null));
                        break;
                    case EventType.ActivityTaskStarted:
                        var started = @event.ActivityTaskStartedEventAttributes;
                        if (byScheduledId.TryGetValue(started.ScheduledEventId, out var startedIndex))
                        {
                            runScheduled[startedIndex] = runScheduled[startedIndex] with { Started = @event.EventTime.ToDateTimeOffset(), Attempt = started.Attempt };
                        }

                        break;
                    case EventType.ActivityTaskCompleted:
                        var completed = @event.ActivityTaskCompletedEventAttributes;
                        if (byScheduledId.TryGetValue(completed.ScheduledEventId, out var completedIndex))
                        {
                            runScheduled[completedIndex] = runScheduled[completedIndex] with { Completed = @event.EventTime.ToDateTimeOffset() };
                        }

                        break;
                }
            }

            scheduled.InsertRange(0, runScheduled);
            next = history.Events.FirstOrDefault()?.WorkflowExecutionStartedEventAttributes?.ContinuedExecutionRunId;
            if (string.IsNullOrEmpty(next) || !visited.Add(next))
            {
                return scheduled;
            }

            runId = next;
        }
    }

    /// <summary>The sum of every captured point of a counter on the orchestration worker, by its tags.</summary>
    public static async Task<IReadOnlyList<CapturedPoint>> CaptureAsync(MultiBlockStage.Composition composition, string resource, string instrument, CancellationToken cancellationToken)
    {
        using var client = composition.App.CreateHttpClient(resource, "http");
        using var response = await client.GetAsync(new Uri("/diagnostics/telemetry/metrics", UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var points = new List<CapturedPoint>();
        foreach (var point in document.RootElement.GetProperty("points").EnumerateArray())
        {
            if (point.GetProperty("instrument").GetString() != instrument)
            {
                continue;
            }

            var value = point.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble()
                : point.TryGetProperty("sum", out var sum) && sum.ValueKind == JsonValueKind.Number ? sum.GetDouble()
                : 0d;
            var tags = point.GetProperty("tags").EnumerateObject().ToDictionary(tag => tag.Name, tag => tag.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
            points.Add(new CapturedPoint(value, tags));
        }

        return points;
    }

    /// <summary>
    /// The current total of a cumulative counter in the capture. The capture keeps every periodic
    /// export, and each export repeats the series' running total, so the total is the latest -
    /// the largest - value of each series, summed over the series; never the sum of the points.
    /// </summary>
    /// <param name="points">The captured points of one counter.</param>
    public static double Cumulative(IEnumerable<CapturedPoint> points) =>
        points
            .GroupBy(point => string.Join('|', point.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal).Select(tag => tag.Key + "=" + tag.Value)), StringComparer.Ordinal)
            .Sum(series => series.Max(point => point.Value));

    /// <summary>Writes a case's figures to the run's evidence.</summary>
    public static void Evidence(string fileName, string text)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(text);
        RunEnvironment.WriteEvidence(fileName, Encoding.UTF8.GetBytes(text));
    }

    /// <inheritdoc />
    public void Dispose() => Stage.Dispose();

    /// <summary>One scheduled activity: its type, its queue, when it was scheduled, started and completed, and the attempt that started.</summary>
    public sealed record ScheduledActivity(string Type, string Queue, DateTimeOffset Scheduled, DateTimeOffset? Started, DateTimeOffset? Completed, int? Attempt);

    /// <summary>One captured measurement and its tags.</summary>
    public sealed record CapturedPoint(double Value, IReadOnlyDictionary<string, string> Tags);
}
