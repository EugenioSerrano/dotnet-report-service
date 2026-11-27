using ReportService.ServiceDefaults;
using ReportService.Workers.Merge;

namespace ReportService.Workers.Merge.Entry;

/// <summary>
/// The merge worker container's entry point. It builds the worker's host and owns
/// the process's lifecycle: a start the configuration refuses ends the process with
/// the decided exit code, after one line naming the setting and the reason, and a
/// host-requested stop ends it with zero. Any other failure is rethrown, so a
/// genuine fault still ends the process loudly.
/// </summary>
public static class MergeWorkerEntryPoint
{
    /// <summary>Runs the process and answers its exit code.</summary>
    /// <param name="args">The command line.</param>
    public static async Task<int> Main(string[] args)
    {
        var app = MergeWorkerProgram.BuildHost(args);
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
                    "The merge worker cannot start: {Refusal} The process ends with exit code {ExitCode}.",
                    refusal.Message,
                    MergeWorkerProgram.RefusedStartExitCode);
                return MergeWorkerProgram.RefusedStartExitCode;
            }

            await app.WaitForShutdownAsync().ConfigureAwait(false);
            return 0;
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

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
