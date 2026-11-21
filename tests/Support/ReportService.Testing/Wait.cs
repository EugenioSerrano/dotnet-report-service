using System.Diagnostics;

namespace ReportService.Testing;

/// <summary>
/// Bounded polling for readiness probes. Only a readiness probe is repeated —
/// never a test assertion — and the wait ends at its deadline with a message
/// naming what was awaited, the elapsed time and the last error.
/// </summary>
public static class Wait
{
    /// <summary>
    /// Repeats <paramref name="probe"/> until it returns true or <paramref name="deadline"/> passes.
    /// This form does not bound an attempt: the probe must return on its own, however long that takes.
    /// Use the overload that takes an attempt timeout for a probe that can block on a dependency.
    /// </summary>
    public static Task UntilAsync(
        string what,
        Func<CancellationToken, Task<bool>> probe,
        TimeSpan deadline,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        // Validated here as well, so a caller that passes a bad argument sees the
        // failure at the call site rather than in the returned task.
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(probe);

        return UntilAsync(what, probe, deadline, interval, Timeout.InfiniteTimeSpan, cancellationToken);
    }

    /// <summary>
    /// Repeats <paramref name="probe"/> until it returns true or <paramref name="deadline"/> passes,
    /// cutting an attempt that has not returned within <paramref name="attemptTimeout"/>. The probe
    /// must honour the cancellation token it is given: that token is the only thing that ends an
    /// attempt which would otherwise read on, so the whole wait ends no later than
    /// <paramref name="deadline"/> plus one attempt. A cancellation the caller causes, or one the
    /// probe raises for its own reason, is never reported as an attempt timeout.
    /// </summary>
    public static async Task UntilAsync(
        string what,
        Func<CancellationToken, Task<bool>> probe,
        TimeSpan deadline,
        TimeSpan interval,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(probe);
        if (attemptTimeout <= TimeSpan.Zero && attemptTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attemptTimeout),
                attemptTimeout,
                "The attempt timeout must be positive, or infinite for an attempt that is not bounded.");
        }

        var bounded = attemptTimeout != Timeout.InfiniteTimeSpan;
        var elapsed = Stopwatch.StartNew();
        Exception? lastError = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The attempt's own source exists only while the attempt is bounded; it is
            // disposed at the end of the attempt, after being cancelled at most once.
            using CancellationTokenSource? attempt = bounded
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (attempt is not null)
            {
                attempt.CancelAfter(attemptTimeout);
            }

            var attemptToken = attempt?.Token ?? cancellationToken;
            try
            {
                if (await probe(attemptToken).ConfigureAwait(false))
                {
                    return;
                }

                lastError = null;
            }
            catch (OperationCanceledException)
                when (attempt is not null
                      && attempt.IsCancellationRequested
                      && !cancellationToken.IsCancellationRequested)
            {
                // Only the attempt's own bound fired: the dependency has not answered yet,
                // which is not a failed wait.
                lastError = new TimeoutException(
                    $"the attempt did not answer within {attemptTimeout.TotalSeconds:F1} s");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
            }

            if (elapsed.Elapsed >= deadline)
            {
                var detail = lastError is null ? string.Empty : $" Last error: {lastError.Message}";
                throw new TimeoutException(
                    $"{what} was not ready after {elapsed.Elapsed.TotalSeconds:F1} s (deadline {deadline.TotalSeconds:F0} s).{detail}",
                    lastError);
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
