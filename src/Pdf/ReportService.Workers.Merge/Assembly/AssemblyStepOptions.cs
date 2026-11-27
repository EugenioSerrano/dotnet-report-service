using ReportService.Jobs;
using System.Text;
using Temporalio.Common;
using Temporalio.Workflows;

namespace ReportService.Workers.Merge.Assembly;

/// <summary>
/// The execution settings of one assembly: the merge queue, the beat it must keep,
/// the bound of one attempt and the waits the engine keeps between attempts. The
/// values are the architecture's (ADR-012 §3.8) and are asserted on their own
/// without an engine; the attempt's object key belongs to the attempt, so a retry
/// writes its own object and never overwrites another attempt's.
/// </summary>
public static class AssemblyStepOptions
{
    /// <summary>The queue the assembly is served from - the merge worker's one queue.</summary>
    public const string Queue = "report-pdf-merge";

    /// <summary>The queue of <paramref name="priority"/> for this kind of work.</summary>
    /// <param name="priority">The class.</param>
    public static string QueueFor(JobPriority priority) => PriorityTaskQueues.For(Queue, priority);

    /// <summary>The queue this process serves for this kind of work, from its served-class setting.</summary>
    /// <param name="servedClassSetting">The value of <c>Priority:ServedClass</c>, or none.</param>
    public static string ServedQueue(string? servedClassSetting) => PriorityTaskQueues.ServedQueue(Queue, servedClassSetting);

    /// <summary>How often a running assembly reports that it is still working.</summary>
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(5);

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

    /// <summary>
    /// The codes that are never retried: a document that failed its structural
    /// verification, an assembly over the ceiling, a package that cannot be
    /// stamped, and a state the step cannot use. A temporary-disk quota refusal is
    /// not here: its cause may be transient.
    /// </summary>
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

    /// <summary>The identity of one attempt, the value that makes its object key its own.</summary>
    /// <param name="workflowId">The identifier of the execution that started the step.</param>
    /// <param name="workflowRunId">The identifier of the run of that execution.</param>
    /// <param name="activityId">The identifier of the step.</param>
    /// <param name="attempt">The attempt number, from one.</param>
    public static string AttemptId(string workflowId, string workflowRunId, string activityId, int attempt)
    {
        var text = $"{workflowId}\n{workflowRunId}\n{activityId}\n{attempt}";
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return digest[..24];
    }
}
