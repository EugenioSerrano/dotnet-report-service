using Microsoft.Extensions.Logging.Abstractions;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Workers.Orchestration.Telemetry;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// Two units of work with two job identifiers, one metrics object each: the outcome
/// recorded inside a unit reaches that unit's capture and not the other's, and an
/// outcome recorded outside any unit reaches no capture at all. The identifier is the
/// product's own unit of work, and it is never a dimension of a measurement, so the
/// capture is the only place it is visible.
/// </summary>
public sealed class JobOutcomeCaptureIsolationTests
{
    private const string FirstJob = "synthetic-first-job";

    private const string SecondJob = "synthetic-second-job";

    private const string FirstCode = "INPUT_NOT_FOUND";

    private const string SecondCode = "TEMPLATE_NOT_FOUND";

    [Fact]
    public void AnOutcomeOfOneUnitIsNotInTheOtherUnitsCapture()
    {
        using var first = new JobOutcomeCapture(FirstJob);
        using var second = new JobOutcomeCapture(SecondJob);
        using var firstMetrics = new OrchestrationMetrics(MeterIdentity.ForHost(TelemetryDefaults.MeterName));
        using var secondMetrics = new OrchestrationMetrics(MeterIdentity.ForHost(TelemetryDefaults.MeterName));

        using (JobContext.Begin(FirstJob, NullLoggerFactory.Instance))
        {
            firstMetrics.JobOutcome(OrchestrationMetrics.FailedJobStatus, FirstCode);
        }

        using (JobContext.Begin(SecondJob, NullLoggerFactory.Instance))
        {
            secondMetrics.JobOutcome(OrchestrationMetrics.FailedJobStatus, SecondCode);
        }

        var inTheFirst = Outcomes(first.Captured);
        Assert.Contains(inTheFirst, measurement => Code(measurement) == FirstCode);
        Assert.Equal([FirstCode], inTheFirst.Select(Code));

        var inTheSecond = Outcomes(second.Captured);
        Assert.Contains(inTheSecond, measurement => Code(measurement) == SecondCode);
        Assert.Equal([SecondCode], inTheSecond.Select(Code));

        var measured = Assert.Single(inTheFirst);
        Assert.Equal(1L, measured.Value);
        Assert.Equal(OrchestrationMetrics.FailedJobStatus, measured.Tag("status"));
    }

    [Fact]
    public void AnOutcomeRecordedOutsideAnyUnitReachesNoCapture()
    {
        using var captured = new JobOutcomeCapture(FirstJob);
        using var metrics = new OrchestrationMetrics(MeterIdentity.ForHost(TelemetryDefaults.MeterName));

        metrics.JobOutcome(OrchestrationMetrics.FailedJobStatus, FirstCode);

        Assert.Empty(captured.Captured);
    }

    private static JobOutcomeCapture.Measurement[] Outcomes(IReadOnlyList<JobOutcomeCapture.Measurement> captured) =>
        [.. captured.Where(measurement => string.Equals(measurement.Name, OrchestrationMetrics.JobOutcomesInstrumentName, StringComparison.Ordinal))];

    private static string? Code(JobOutcomeCapture.Measurement measurement) => measurement.Tag("code") as string;
}
