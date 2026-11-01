using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>
/// The unit of work a job identifier is attached to. While one is open, every
/// log entry written through the process's logger factory carries the
/// identifier as the scope key <c>job_id</c>, and every span started inside it
/// carries the tag <c>job.id</c> (see <see cref="JobIdActivityProcessor"/>).
/// The identifier is <b>never</b> a metric dimension: instruments take constant
/// tags, so it cannot reach a measurement (release §3.11 item 4).
/// <para>
/// A job identifier is an internal identifier, not free text: it must match
/// <c>[a-z0-9._-]{1,128}</c>, and <see cref="Begin"/> refuses anything else.
/// Nesting restores the previous identifier when the inner scope ends.
/// </para>
/// </summary>
public static partial class JobContext
{
    /// <summary>The tag a span carries while it belongs to a unit of work.</summary>
    public const string ActivityTagName = "job.id";

    /// <summary>The scope key a log entry carries while it belongs to a unit of work.</summary>
    public const string ScopeKey = "job_id";

    /// <summary>The longest identifier the grammar accepts.</summary>
    public const int MaxLength = 128;

    private static readonly AsyncLocal<string?> CurrentIdentifier = new();
    private static ILoggerFactory? loggers;

    /// <summary>The identifier of the open unit of work, or <see langword="null"/> outside one.</summary>
    public static string? Current => CurrentIdentifier.Value;

    /// <summary>The logger factory the log scope is opened on; set at start by the registration.</summary>
    internal static ILoggerFactory? LoggerFactory
    {
        get => loggers;
        set => loggers = value;
    }

    /// <summary>
    /// Opens a unit of work for <paramref name="jobId"/>: sets the current
    /// identifier, tags the current span and opens the log scope. The returned
    /// handle restores the previous state when it is disposed.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="jobId"/> does not match the grammar.</exception>
    public static IDisposable Begin(string jobId)
    {
        if (!IsValid(jobId))
        {
            throw new ArgumentException(
                $"A job identifier must be 1 to {MaxLength} characters of a-z, 0-9, '.', '_' or '-'.",
                nameof(jobId));
        }

        var previous = CurrentIdentifier.Value;
        CurrentIdentifier.Value = jobId;
        if (Activity.Current is { } activity && activity.GetTagItem(ActivityTagName) is null)
        {
            activity.SetTag(ActivityTagName, jobId);
        }

        return new UnitOfWork(previous, JobIdLogScope.Start(loggers, jobId));
    }

    /// <summary>Whether <paramref name="jobId"/> is a job identifier the grammar accepts.</summary>
    public static bool IsValid(string? jobId) => jobId is not null && Grammar().IsMatch(jobId);

    [GeneratedRegex("^[a-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex Grammar();

    private sealed class UnitOfWork(string? previous, IDisposable? scope) : IDisposable
    {
        private bool ended;

        public void Dispose()
        {
            if (ended)
            {
                return;
            }

            ended = true;
            scope?.Dispose();
            CurrentIdentifier.Value = previous;
        }
    }
}
