using ReportService.Jobs;

namespace ReportService.Jobs.Tests;

/// <summary>
/// The terminal-state predicate of a job record (US-008.BOLT-002, SPEC-261125-1224 §8.1,
/// Phase D.2/D.4; ADR-048): the tri-state rollup adds <c>PartiallySucceeded</c> to the terminal
/// set, so a mixed multi-output job that ended is terminal and can no longer be transitioned or
/// finalised again. The three states that already ended a job stay terminal, and the states of a
/// job still in flight are not. The oracle is the approved intent (ADR-048 §4), never the
/// implementation (G06).
/// </summary>
public sealed class ReportJobRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ReportJobStatus.Succeeded)]
    [InlineData(ReportJobStatus.Failed)]
    [InlineData(ReportJobStatus.Cancelled)]
    [InlineData(ReportJobStatus.PartiallySucceeded)]
    public void AJobInATerminalStateIsTerminal(ReportJobStatus status) =>
        Assert.True(Record(status).IsTerminal);

    [Theory]
    [InlineData(ReportJobStatus.Queued)]
    [InlineData(ReportJobStatus.Validating)]
    [InlineData(ReportJobStatus.Partitioning)]
    [InlineData(ReportJobStatus.Running)]
    [InlineData(ReportJobStatus.Finalizing)]
    [InlineData(ReportJobStatus.Cancelling)]
    public void AJobStillInFlightIsNotTerminal(ReportJobStatus status) =>
        Assert.False(Record(status).IsTerminal);

    [Fact]
    public void PartiallySucceededIsADistinctTerminalValueAndNotFailedOrSucceeded()
    {
        Assert.NotEqual(ReportJobStatus.Failed, ReportJobStatus.PartiallySucceeded);
        Assert.NotEqual(ReportJobStatus.Succeeded, ReportJobStatus.PartiallySucceeded);
    }

    private static ReportJobRecord Record(ReportJobStatus status) =>
        new("job-0123456789abcdef0123456789abcdef", "dev-consumer", "upload-0123456789abcdef0123456789abcdef", status, Now, Now);
}
