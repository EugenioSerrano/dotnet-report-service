using ReportService.Jobs;
using Temporalio.Common;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The execution settings with which this worker schedules the assembly on the
/// merge worker's queue: every value equals the delivered assembly's own factory
/// (ADR-012 §3.8). The assembly keeps a beat, one attempt is bounded, and a
/// document that failed its verification, an assembly over the ceiling, a package
/// that cannot be stamped or a state the step cannot use is never retried. The
/// integration suite asserts every value against the delivered factory.
/// </summary>
public static class AssemblePdfStepOptions
{
    /// <summary>The queue the assembly is served from - the merge worker's one queue.</summary>
    public const string Queue = "report-pdf-merge";

    /// <summary>How long an assembly may go without a beat before the engine retries it.</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long one attempt of an assembly may run.</summary>
    public static readonly TimeSpan StartToClose = TimeSpan.FromMinutes(15);

    /// <summary>The wait after the first failed attempt.</summary>
    public static readonly TimeSpan InitialInterval = TimeSpan.FromSeconds(2);

    /// <summary>How much each further wait grows.</summary>
    public const float BackoffCoefficient = 2f;

    /// <summary>The longest wait between two attempts.</summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(60);

    /// <summary>How many attempts one assembly gets.</summary>
    public const int MaximumAttempts = 3;

    /// <summary>A constant summary of the policy, as the recorded execution reference states it.</summary>
    public const string RetryPolicySummary = "assemble: heartbeat 5s/30s, start-to-close 15m, backoff 2s x2 max 60s, 3 attempts";

    /// <summary>The codes that are never retried.</summary>
    public static readonly IReadOnlyList<string> NonRetryableErrorTypes =
    [
        "ARTIFACT_INTEGRITY_FAILED",
        "OUTPUT_LIMIT_EXCEEDED",
        "TEMPLATE_INVALID",
        "EXECUTION_FAILED",
    ];

    /// <summary>Builds the execution settings of one assembly.</summary>
    public static ActivityOptions Create() => new()
    {
        TaskQueue = Queue,
        StartToCloseTimeout = StartToClose,
        HeartbeatTimeout = HeartbeatTimeout,
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = InitialInterval,
            BackoffCoefficient = BackoffCoefficient,
            MaximumInterval = MaximumInterval,
            MaximumAttempts = MaximumAttempts,
            NonRetryableErrorTypes = NonRetryableErrorTypes,
        },
    };

    /// <summary>
    /// Builds the execution settings for a job dispatched on <paramref name="priority"/>: the
    /// class's own queue, every other value as <see cref="Create()"/> builds it.
    /// </summary>
    /// <param name="priority">The job's effective class.</param>
    public static ActivityOptions Create(JobPriority priority)
    {
        var options = Create();
        options.TaskQueue = PriorityTaskQueues.For(Queue, priority);
        return options;
    }
}
