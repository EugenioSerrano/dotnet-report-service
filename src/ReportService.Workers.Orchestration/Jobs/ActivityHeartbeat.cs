namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The beat of a step that runs for a while: while the pass it reports on is running, it
/// says the step is still working once per interval, with the progress the pass publishes.
/// It runs beside the pass rather than inside it, so a slow read or a slow write cannot
/// silence the beat, and it ends with the pass. A step that loses its beat is retried from
/// the start of its work, which is why the interval is chosen against the timeout the
/// engine is given and not against the work itself.
/// </summary>
public static class ActivityHeartbeat
{
    /// <summary>
    /// Reports <paramref name="progress"/> every <paramref name="every"/> until
    /// <paramref name="cancellationToken"/> is cancelled, then returns.
    /// </summary>
    /// <param name="every">How often to report; must be greater than zero.</param>
    /// <param name="progress">What the pass has done so far, read at every beat.</param>
    /// <param name="beat">Reports one beat with the progress read for it.</param>
    /// <param name="cancellationToken">Cancelled when the pass ends.</param>
    public static async Task WhileRunningAsync(
        TimeSpan every,
        Func<long> progress,
        Action<long> beat,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(beat);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(every, TimeSpan.Zero);

        using var timer = new PeriodicTimer(every);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                beat(progress());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The pass has ended: there is nothing left to report.
        }
    }
}
