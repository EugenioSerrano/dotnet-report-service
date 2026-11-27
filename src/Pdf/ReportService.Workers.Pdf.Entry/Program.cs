using ReportService.ServiceDefaults;
using ReportService.Workers.Pdf;

namespace ReportService.Workers.Pdf.Entry;

/// <summary>
/// The render worker container's entry point. It builds the worker's host and owns the
/// process's lifecycle: a start the configuration refuses ends the process with the decided
/// exit code, after one line naming the setting and the reason and while the host is still
/// alive, and a host-requested stop ends it with zero. Any other failure is rethrown, so a
/// genuine fault still ends the process loudly. The host is built once, by the worker, so the
/// container and the in-process host run the same process.
/// </summary>
public static class PdfWorkerEntryPoint
{
    /// <summary>Runs the process and answers its exit code.</summary>
    /// <param name="args">The command line.</param>
    public static async Task<int> Main(string[] args)
    {
        var app = PdfWorkerProgram.BuildHost(args);

        // Captured before the start: the refusal line must not depend on a logger read after the
        // host was disposed.
        var logger = app.Logger;
        try
        {
            try
            {
                await app.StartAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (Refusal(exception) is { } refusal)
            {
                logger.LogError(
                    "The render worker cannot start: {Refusal} The process ends with exit code {ExitCode}.",
                    refusal.Message,
                    PdfWorkerProgram.RefusedStartExitCode);
                return PdfWorkerProgram.RefusedStartExitCode;
            }

            await app.WaitForShutdownAsync().ConfigureAwait(false);
            return 0;
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The start refusal a failure carries: the error itself, its inner, or any of an aggregate's
    // flattened inners. Anything else is not a refused start.
    private static ConfigurationError? Refusal(Exception exception)
    {
        if (exception is ConfigurationError direct)
        {
            return direct;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions
                .Select(Refusal)
                .FirstOrDefault(found => found is not null);
        }

        return exception.InnerException is { } inner ? Refusal(inner) : null;
    }
}
