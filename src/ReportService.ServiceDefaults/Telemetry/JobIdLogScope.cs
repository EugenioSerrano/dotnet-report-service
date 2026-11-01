using Microsoft.Extensions.Logging;

namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>
/// The log scope a unit of work opens: one key, <c>job_id</c>, rendered on every
/// entry written while it is open because the log pipeline includes scopes and
/// formatted messages. Only the identifier is put in the scope — never a path,
/// a setting value or report content.
/// </summary>
internal static class JobIdLogScope
{
    private const string Category = "ReportService.Job";

    /// <summary>Opens the scope on <paramref name="loggers"/>; <see langword="null"/> when no usable factory is registered.</summary>
    public static IDisposable? Start(ILoggerFactory? loggers, string jobId)
    {
        if (loggers is null)
        {
            return null;
        }

        try
        {
            ILogger logger = loggers.CreateLogger(Category);
            return logger.BeginScope(new[] { new KeyValuePair<string, object?>(JobContext.ScopeKey, jobId) });
        }
        catch (ObjectDisposedException)
        {
            // A process whose host is already shutting down opens no scope: the entry it
            // writes goes to the loggers that are still alive, without the identifier.
            return null;
        }
    }
}
