using System.Diagnostics;
using System.Globalization;
using System.Text;
using ReportService.Testing;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Workers.Orchestration.Telemetry;

namespace ReportService.EndToEndTests;

/// <summary>
/// The operator's view per priority class over the running composition: the pending-work level
/// is captured for each of the six render and merge class queues, a starved low queue reads its
/// waiting work, the schedule-to-start latency is captured per class queue on the worker that
/// served it, and no captured attribute of the three priority instruments carries a job
/// identifier. The host's memory with the per-class workers running is recorded.
/// </summary>
public sealed class JobPriorityTelemetryEndToEndTests : IDisposable
{
    private static readonly TimeSpan JobDeadline = TimeSpan.FromMinutes(20);

    private static readonly string[] SixQueues =
    [
        "report-pdf-rendering-high", "report-pdf-rendering", "report-pdf-rendering-low",
        "report-pdf-merge-high", "report-pdf-merge", "report-pdf-merge-low",
    ];

    private readonly PriorityComposition _priority = new();

    /// <inheritdoc />
    public void Dispose() => _priority.Dispose();

    [Fact]
    public async Task PendingWorkAndLatencyAreReadPerClassQueueWithoutAJobIdentifier()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _priority.Stage.StartAsync(cancellationToken);
        await RecordHostMemoryAsync(cancellationToken);
        await _priority.Stage.TakeTemplateAsync(composition, cancellationToken);
        await RestartHarness.StopAsync(composition.App, PriorityComposition.LowRenderWorker, cancellationToken);

        var low = await _priority.SubmitAsync(composition, "low", records: null, cancellationToken);

        // The level of the starved low render queue rises; all six queues report a value.
        IReadOnlyList<PriorityComposition.CapturedPoint> pending = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await Wait.UntilAsync(
            "a pending value for each class queue and work waiting on the low render queue",
            async token =>
            {
                pending = await PriorityComposition.CaptureAsync(composition, RestartHarness.OrchestrationWorkerName, OrchestrationMetrics.TaskQueuePendingInstrumentName, token);
                foreach (var point in pending)
                {
                    seen.Add(point.Tags["task_queue"]);
                }

                return seen.IsSupersetOf(SixQueues)
                    && pending.Any(point => point.Tags["task_queue"] == "report-pdf-rendering-low" && point.Value > 0);
            },
            TimeSpan.FromMinutes(3),
            TimeSpan.FromSeconds(2),
            cancellationToken);
        Assert.All(pending, point => Assert.Equal(["kind", "priority_class", "task_queue"], point.Tags.Keys.Order(StringComparer.Ordinal)));
        Assert.All(pending, point => Assert.DoesNotContain(low, string.Join('|', point.Tags.Values), StringComparison.Ordinal));

        await RestartHarness.StartAsync(composition.App, PriorityComposition.LowRenderWorker, cancellationToken);
        Assert.Equal("SUCCEEDED", (await _priority.Stage.WaitForEndAsync(composition, low, cancellationToken, JobDeadline)).Status);

        // The latency is read on the workers that served the job's work, per class queue.
        var latencies = new List<PriorityComposition.CapturedPoint>();
        foreach (var worker in new[] { PriorityComposition.LowRenderWorker, "render-worker", "merge-worker", PriorityComposition.LowMergeWorker })
        {
            latencies.AddRange(await PriorityComposition.CaptureAsync(composition, worker, ScheduleToStartRecorder.InstrumentName, cancellationToken));
        }

        Assert.Contains(latencies, point => point.Tags["task_queue"] == "report-pdf-rendering-low" && point.Tags["priority_class"] == "low");
        Assert.All(latencies, point => Assert.Equal(["kind", "priority_class", "task_queue"], point.Tags.Keys.Order(StringComparer.Ordinal)));
        Assert.All(latencies, point => Assert.DoesNotContain(low, string.Join('|', point.Tags.Values), StringComparison.Ordinal));

        var promotions = await PriorityComposition.CaptureAsync(composition, RestartHarness.OrchestrationWorkerName, OrchestrationMetrics.PriorityPromotionsInstrumentName, cancellationToken);
        Assert.All(promotions, point => Assert.DoesNotContain(low, string.Join('|', point.Tags.Values), StringComparison.Ordinal));

        PriorityComposition.Evidence(
            "priority-telemetry.txt",
            string.Create(CultureInfo.InvariantCulture, $"pending_queues={string.Join(',', seen.Order(StringComparer.Ordinal))} latency_series={latencies.Count}{Environment.NewLine}"));
    }

    // The host's memory with the per-class workers up: one reading of every container's usage.
    private static async Task RecordHostMemoryAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("docker", ["stats", "--no-stream", "--format", "{{.Name}} {{.MemUsage}}"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        RunEnvironment.WriteEvidence("priority-composition-memory.txt", Encoding.UTF8.GetBytes(output));
        TestContext.Current.TestOutputHelper?.WriteLine(output);
    }
}
