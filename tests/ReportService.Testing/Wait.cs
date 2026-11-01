using System.Diagnostics;

namespace ReportService.Testing;

/// <summary>
/// Bounded polling for readiness probes. Only a readiness probe is repeated —
/// never a test assertion — and the wait ends at its deadline with a message
/// naming what was awaited, the elapsed time and the last error.
/// </summary>
public static class Wait
{
    /// <summary>Repeats <paramref name="probe"/> until it returns true or <paramref name="deadline"/> passes.</summary>
    public static async Task UntilAsync(
        string what,
        Func<CancellationToken, Task<bool>> probe,
        TimeSpan deadline,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(probe);

        var elapsed = Stopwatch.StartNew();
        Exception? lastError = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await probe(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                lastError = null;
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
