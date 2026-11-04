using System.Diagnostics;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Api.Tests.Telemetry;

/// <summary>
/// Two telemetry hosts alive at the same time, each with a unit of work of its own:
/// each capture holds the span started inside its own unit and nothing of the other
/// host's, and a capture with no unit open holds nothing. The class runs with the
/// telemetry collection because it starts two hosts at once and a host's configuration
/// reads the process-wide environment entry the collection's cases drive; the capture it
/// proves is correct whichever cases run beside it, and it still runs beside every case
/// of the project outside that collection.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class CaptureIsolationTests
{
    private const string FirstUnitSpan = "span-of-the-first-unit";

    private const string SecondUnitSpan = "span-of-the-second-unit";

    [Fact]
    public async Task EachCaptureHoldsTheSpanOfItsOwnUnitAndNotTheOtherHosts()
    {
        await using var first = await TelemetryTestHost.StartAsync();
        await using var second = await TelemetryTestHost.StartAsync();
        using var source = new ActivitySource(TelemetryDefaults.ActivitySourceName);

        // Each host's span is started while the unit of that host is current, and both
        // units stay alive until both captures are read: neither read can win a race.
        using var firstUnit = first.BeginUnit();
        using (var span = source.StartActivity(FirstUnitSpan))
        {
            Assert.NotNull(span);
        }

        using var secondUnit = second.BeginUnit();
        using (var span = source.StartActivity(SecondUnitSpan))
        {
            Assert.NotNull(span);
        }

        var capturedByTheFirst = first.Activities;
        Assert.Contains(capturedByTheFirst, activity => activity.OperationName == FirstUnitSpan);
        Assert.Equal([FirstUnitSpan], capturedByTheFirst.Select(activity => activity.OperationName));
        Assert.All(capturedByTheFirst, activity => Assert.Equal(firstUnit.TraceId, activity.TraceId));

        var capturedByTheSecond = second.Activities;
        Assert.Contains(capturedByTheSecond, activity => activity.OperationName == SecondUnitSpan);
        Assert.Equal([SecondUnitSpan], capturedByTheSecond.Select(activity => activity.OperationName));
        Assert.All(capturedByTheSecond, activity => Assert.Equal(secondUnit.TraceId, activity.TraceId));
    }

    [Fact]
    public async Task ACaptureWithNoUnitOpenHoldsNothing()
    {
        await using var host = await TelemetryTestHost.StartAsync();
        using var source = new ActivitySource(TelemetryDefaults.ActivitySourceName);

        using (var span = source.StartActivity("span-started-outside-any-unit"))
        {
            Assert.NotNull(span);
        }

        Assert.Empty(host.Activities);
    }
}
