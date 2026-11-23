using System.Diagnostics;

namespace ReportService.Testing;

/// <summary>
/// Drains a child process's standard output and standard error and waits for it to
/// exit, returning the two captured strings. This is the one place the test harness
/// consumes a redirected child's streams, so that no caller reads one stream to
/// completion before starting the other — a sequential drain deadlocks when the child
/// fills the unread stream's OS pipe buffer while the parent is still blocked on the
/// other.
/// </summary>
public static class ChildProcessStreams
{
    /// <summary>
    /// Reads both of <paramref name="process"/>'s redirected streams to completion and
    /// awaits its exit, returning the captured standard output and standard error. The
    /// caller still reads <see cref="Process.ExitCode"/> itself; this method does not
    /// surface it. Both redirected streams are consumed so neither can back-pressure
    /// the other.
    /// </summary>
    /// <param name="process">A started process with both output streams redirected.</param>
    /// <param name="cancellationToken">Cancels the drain and the wait.</param>
    /// <returns>The captured standard output and standard error.</returns>
    public static async Task<(string StdOut, string StdErr)> DrainToExitAsync(
        Process process,
        CancellationToken cancellationToken = default)
    {
        var outTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
    }
}
