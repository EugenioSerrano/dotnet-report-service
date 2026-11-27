using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;

namespace ReportService.Api.Tests.WorkflowEngine;

/// <summary>
/// A render or merge worker's served class, checked at start, and the schedule-to-start
/// latency it records per attempt with the queue, the kind and the class only.
/// </summary>
public sealed class PriorityWorkerSettingsTests
{
    [Fact]
    public void AnAcceptedServedClassHasNoProblem() =>
        Assert.Empty(ServedClassOptions.Problems("high", static _ => true));

    [Fact]
    public void ARefusedServedClassNamesTheSettingAndNeverTheValue()
    {
        var problem = Assert.Single(ServedClassOptions.Problems("high,low", static _ => false));
        Assert.Equal("Priority:ServedClass", problem.Setting);
        Assert.Equal(ServedClassOptions.Reason, problem.Reason);
        Assert.DoesNotContain("high,low", problem.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingIsReadFromTheProcessConfiguration()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration["Priority:ServedClass"] = "low";
        Assert.Equal("low", ServedClassOptions.Read(builder));
        Assert.Throws<ArgumentNullException>(() => ServedClassOptions.Read(null!));
        Assert.Throws<ArgumentNullException>(() => ServedClassOptions.Problems(null, null!));
    }

    [Fact]
    public void AnAttemptsWaitIsRecordedWithTheQueueTheKindAndTheClassOnly()
    {
        var identity = MeterIdentity.ForHost("ReportService.Tests.ScheduleToStart");
        var recorded = new List<(double Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == identity.Name && instrument.Name == ScheduleToStartRecorder.InstrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) => recorded.Add((value, tags.ToArray())));
        listener.Start();

        using var recorder = new ScheduleToStartRecorder(identity, "render", static queue => queue.EndsWith("-high", StringComparison.Ordinal) ? "high" : "normal");
        var scheduled = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        recorder.Record("report-pdf-rendering-high", scheduled, scheduled.AddSeconds(2.5));
        recorder.Record("report-pdf-rendering", scheduled, scheduled.AddSeconds(-1));

        Assert.Equal(2, recorded.Count);
        Assert.Equal(2.5, recorded[0].Value);
        Assert.Equal(0, recorded[1].Value);
        Assert.Equal(
            ["kind", "priority_class", "task_queue"],
            recorded[0].Tags.Select(tag => tag.Key).Order(StringComparer.Ordinal));
        Assert.Equal("high", recorded[0].Tags.Single(tag => tag.Key == "priority_class").Value);
        Assert.Equal("normal", recorded[1].Tags.Single(tag => tag.Key == "priority_class").Value);
    }

    [Fact]
    public void TheRecorderValidatesItsArguments()
    {
        var identity = MeterIdentity.ForHost("ReportService.Tests.ScheduleToStartArguments");
        Assert.Throws<ArgumentNullException>(() => new ScheduleToStartRecorder(null!, "render", static q => q));
        Assert.Throws<ArgumentException>(() => new ScheduleToStartRecorder(identity, " ", static q => q));
        Assert.Throws<ArgumentNullException>(() => new ScheduleToStartRecorder(identity, "render", null!));
        using var recorder = new ScheduleToStartRecorder(identity, "merge", static q => q);
        Assert.Throws<ArgumentException>(() => recorder.Record("", DateTime.UnixEpoch, DateTime.UnixEpoch));
    }
}
