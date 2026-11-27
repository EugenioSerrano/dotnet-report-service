using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The beat of a long step, asserted on its own without an engine: it reports the progress
/// it is asked for, keeps reporting at its interval while the pass runs, and stops as soon
/// as the pass ends.
/// </summary>
public sealed class ActivityHeartbeatTests
{
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task TheBeatRepeatsUntilThePassEnds()
    {
        using var passEnds = new CancellationTokenSource();
        var beats = new List<long>();
        var recordsRead = 0L;

        await ActivityHeartbeat.WhileRunningAsync(
            Every,
            () => Interlocked.Read(ref recordsRead),
            count =>
            {
                beats.Add(count);
                if (beats.Count == 3)
                {
                    passEnds.Cancel();
                }
            },
            passEnds.Token);

        Assert.Equal(3, beats.Count);
        Assert.All(beats, beat => Assert.Equal(0, beat));
    }

    [Fact]
    public async Task TheBeatCarriesHowManyRecordsThePassHasAdmitted()
    {
        using var passEnds = new CancellationTokenSource();
        var recordsRead = 0L;
        var last = -1L;

        await ActivityHeartbeat.WhileRunningAsync(
            Every,
            () => Interlocked.Increment(ref recordsRead),
            count =>
            {
                last = count;
                passEnds.Cancel();
            },
            passEnds.Token);

        Assert.Equal(1, last);
    }

    [Fact]
    public async Task ABeatThatIsAlreadyStoppedReportsNothing()
    {
        using var passEnded = new CancellationTokenSource();
        await passEnded.CancelAsync();
        var beats = 0;

        await ActivityHeartbeat.WhileRunningAsync(Every, () => 1, _ => beats++, passEnded.Token);

        Assert.Equal(0, beats);
    }

    [Fact]
    public async Task AnIntervalThatIsNotPositiveIsRefused()
    {
        var failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ActivityHeartbeat.WhileRunningAsync(TimeSpan.Zero, () => 0, _ => { }, CancellationToken.None));

        Assert.Equal("every", failure.ParamName);
    }
}
