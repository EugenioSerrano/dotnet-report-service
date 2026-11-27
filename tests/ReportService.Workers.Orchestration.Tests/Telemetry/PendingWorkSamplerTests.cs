using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.ServiceDefaults;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Api.TaskQueue.V1;
using Temporalio.Api.WorkflowService.V1;

namespace ReportService.Workers.Orchestration.Tests.Telemetry;

/// <summary>
/// The pending-work level per priority class: the six render and merge queues read through a
/// built engine description; each value kept with its queue, kind and class and nothing else;
/// a queue that cannot be read reporting no value.
/// </summary>
public sealed class PendingWorkSamplerTests
{
    [Fact]
    public void ThePendingCountIsTheQueuesApproximateBacklog()
    {
        Assert.Equal(7, PendingWorkMapper.Pending(new DescribeTaskQueueResponse { Stats = new TaskQueueStats { ApproximateBacklogCount = 7 } }));
        Assert.Null(PendingWorkMapper.Pending(new DescribeTaskQueueResponse()));
        Assert.Throws<ArgumentNullException>(() => PendingWorkMapper.Pending(null!));
    }

    [Fact]
    public void TheSixQueuesAreRenderAndMergeEachClass() =>
        Assert.Equal(
            [
                ("report-pdf-rendering-high", "render", "high"),
                ("report-pdf-rendering", "render", "normal"),
                ("report-pdf-rendering-low", "render", "low"),
                ("report-pdf-merge-high", "merge", "high"),
                ("report-pdf-merge", "merge", "normal"),
                ("report-pdf-merge-low", "merge", "low"),
            ],
            PendingWorkMapper.Queues);

    [Fact]
    public async Task EachQueuesValueIsKeptWithItsQueueKindAndClassOnly()
    {
        var (sampler, observe, dispose) = Create((queue, _) =>
            Task.FromResult(new DescribeTaskQueueResponse { Stats = new TaskQueueStats { ApproximateBacklogCount = queue.Length } }));
        using var _ = dispose;

        Assert.Equal(0, await sampler.SampleOnceAsync(TestContext.Current.CancellationToken));

        var measurements = observe();
        Assert.Equal(6, measurements.Count);
        var high = measurements.Single(m => m.Tag("task_queue") == "report-pdf-rendering-high");
        Assert.Equal("report-pdf-rendering-high".Length, high.Value);
        Assert.Equal("render", high.Tag("kind"));
        Assert.Equal("high", high.Tag("priority_class"));
        Assert.All(measurements, m => Assert.Equal(["kind", "priority_class", "task_queue"], m.Tags.Select(t => t.Key).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task AQueueThatCannotBeReadReportsNoValue()
    {
        var failNow = false;
        var (sampler, observe, dispose) = Create((queue, _) =>
            failNow && queue == "report-pdf-merge-low"
                ? Task.FromException<DescribeTaskQueueResponse>(new InvalidOperationException("the engine is down"))
                : Task.FromResult(new DescribeTaskQueueResponse { Stats = new TaskQueueStats { ApproximateBacklogCount = 3 } }));
        using var _ = dispose;

        await sampler.SampleOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(6, observe().Count);

        failNow = true;
        Assert.Equal(1, await sampler.SampleOnceAsync(TestContext.Current.CancellationToken));
        var measurements = observe();
        Assert.Equal(5, measurements.Count);
        Assert.DoesNotContain(measurements, m => m.Tag("task_queue") == "report-pdf-merge-low");
    }

    [Theory]
    [InlineData("00:00:01", true)]
    [InlineData("00:00:15", true)]
    [InlineData("00:05:00", true)]
    [InlineData("00:00:00.500", false)]
    [InlineData("00:05:01", false)]
    public void TheSampleIntervalIsBetweenOneSecondAndFiveMinutes(string interval, bool valid)
    {
        var options = new PendingWorkOptions { SampleInterval = TimeSpan.Parse(interval, System.Globalization.CultureInfo.InvariantCulture) };
        Assert.Equal(valid, !options.Validate().Any());
    }

    private static (PendingWorkSampler Sampler, Func<List<Observed>> Observe, IDisposable Dispose) Create(
        Func<string, CancellationToken, Task<DescribeTaskQueueResponse>> describe)
    {
        var identity = MeterIdentity.ForHost("ReportService.Tests.PendingWork." + Guid.NewGuid().ToString("N"));
        var metrics = new OrchestrationMetrics(identity);
        var observed = new List<Observed>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == identity.Name && instrument.Name == OrchestrationMetrics.TaskQueuePendingInstrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => observed.Add(new Observed(value, tags.ToArray())));
        listener.Start();

        List<Observed> Observe()
        {
            observed.Clear();
            listener.RecordObservableInstruments();
            return [.. observed];
        }

        var sampler = new PendingWorkSampler(describe, metrics, Options.Create(new PendingWorkOptions()), NullLogger<PendingWorkSampler>.Instance);
        return (sampler, Observe, new Disposables(listener, metrics));
    }

    private sealed record Observed(long Value, KeyValuePair<string, object?>[] Tags)
    {
        public string? Tag(string name) => Tags.FirstOrDefault(t => t.Key == name).Value as string;
    }

    private sealed class Disposables(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }
    }
}
