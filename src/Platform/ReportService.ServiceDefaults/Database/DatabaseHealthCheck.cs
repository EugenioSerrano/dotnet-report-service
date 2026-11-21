using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ReportService.ServiceDefaults;

/// <summary>
/// Reports whether the metadata database is reachable and at the schema version
/// the process expects. It never throws, it answers within a bounded time, and
/// it puts no host, user, password or exception text in its description or its
/// data: only the expected and the applied version, or the word
/// <c>unreachable</c>.
/// </summary>
public sealed partial class DatabaseHealthCheck(
    ISchemaJournalReader reader,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseHealthCheck> logger) : IHealthCheck
{
    /// <summary>The name the check carries in a health body.</summary>
    public const string Name = "metadata-database";

    /// <summary>How long the journal read may take before the database is reported unreachable.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private string? _lastState;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        var expected = options.Value.ExpectedSchemaVersion;
        IReadOnlyList<string>? applied = null;
        Exception? failure = null;
        var timedOut = false;

        try
        {
            var reading = reader.ReadAppliedScriptsAsync(cancellationToken);
            if (await Task.WhenAny(reading, Task.Delay(ReadTimeout, cancellationToken)).ConfigureAwait(false) == reading)
            {
                applied = await reading.ConfigureAwait(false);
            }
            else
            {
                Observe(reading);
                timedOut = true;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            failure = exception;
        }

        if (applied is null)
        {
            if (Change("unreachable"))
            {
                LogNotReady("unreachable", expected, SchemaVersion.None, timedOut ? string.Empty : Detail(failure));
            }

            return HealthCheckResult.Unhealthy(
                $"metadata database not ready: unreachable; expected {expected}, applied {SchemaVersion.None}",
                data: new Dictionary<string, object>
                {
                    ["expected"] = expected,
                    ["error"] = "unreachable",
                });
        }

        var assessment = SchemaVersion.Assess(expected, applied);
        if (assessment.Status == SchemaStatus.Current)
        {
            if (Change("current"))
            {
                LogReady(assessment.Applied);
            }

            return HealthCheckResult.Healthy(
                $"metadata database ready at {assessment.Applied}",
                new Dictionary<string, object>
                {
                    ["expected"] = expected,
                    ["applied"] = assessment.Applied,
                });
        }

        var reason = assessment.Status.ToString().ToLowerInvariant();
        if (Change(reason))
        {
            LogNotReady(reason, expected, assessment.Applied, string.Empty);
        }

        return HealthCheckResult.Unhealthy(
            $"metadata database not ready: {reason}; expected {expected}, applied {assessment.Applied}",
            data: new Dictionary<string, object>
            {
                ["expected"] = expected,
                ["applied"] = assessment.Applied,
            });
    }

    /// <summary>The exception's type and SQLSTATE only: never its message, which can name the host and the user.</summary>
    private static string Detail(Exception? exception)
    {
        if (exception is null)
        {
            return string.Empty;
        }

        var postgres = exception as PostgresException ?? exception.InnerException as PostgresException;
        return postgres is null ? exception.GetType().Name : $"{exception.GetType().Name} {postgres.SqlState}";
    }

    /// <summary>Observes a read that was abandoned at the deadline, so its exception is never unobserved.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Records the state and reports whether it changed, so a steady state logs once.</summary>
    private bool Change(string state)
    {
        lock (_gate)
        {
            if (string.Equals(_lastState, state, StringComparison.Ordinal))
            {
                return false;
            }

            _lastState = state;
            return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "metadata database ready at {Version}")]
    private partial void LogReady(string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "metadata database not ready: {Reason}; expected {Expected}, applied {Applied}{Detail}")]
    private partial void LogNotReady(string reason, string expected, string applied, string detail);
}
