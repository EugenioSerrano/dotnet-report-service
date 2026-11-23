using System.Text;
using Temporalio.Common;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The execution settings of a data step: the queue it is served from, the beat it
/// must keep, the bound of one attempt and the waits the engine keeps between
/// attempts. A step that loses its beat is retried from the start of its work, so
/// the settings are built from constants and asserted on their own without an engine.
/// </summary>
public static class PdfOutputActivityOptions
{
    /// <summary>The queue the data steps are served from.</summary>
    public const string DataQueue = "report-data";

    /// <summary>How often a running step reports that it is still working.</summary>
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(5);

    /// <summary>How long a step may go without a beat before the engine retries it.</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long one attempt may run.</summary>
    public static readonly TimeSpan StartToClose = TimeSpan.FromHours(1);

    /// <summary>The wait after the first failed attempt.</summary>
    public static readonly TimeSpan InitialInterval = TimeSpan.FromSeconds(2);

    /// <summary>How much each further wait grows.</summary>
    public const float BackoffCoefficient = 2f;

    /// <summary>The longest wait between two attempts.</summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(60);

    /// <summary>How many attempts a data step gets.</summary>
    public const int MaximumAttempts = 5;

    /// <summary>A constant summary of the policy, as the recorded execution reference states it.</summary>
    public const string RetryPolicySummary =
        "data: heartbeat 60s, start-to-close 1h, backoff 2s x2 max 60s, 5 attempts";

    /// <summary>The codes that are never retried: a refusal of the data is not a transient fault.</summary>
    public static readonly IReadOnlyList<string> NonRetryableErrorTypes =
    [
        "DATA_INVALID",
        "RECORD_TOO_LARGE",
        "QUOTA_EXCEEDED",
        "EXECUTION_FAILED",
        "TEMPLATE_INVALID",
    ];

    /// <summary>Builds the execution settings of one data step.</summary>
    public static ActivityOptions Create() => new()
    {
        TaskQueue = DataQueue,
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

    /// <summary>The identity of one attempt, the value that makes its object keys its own.</summary>
    /// <param name="workflowId">The identifier of the execution that started the step.</param>
    /// <param name="workflowRunId">The identifier of the run of that execution.</param>
    /// <param name="activityId">The identifier of the step.</param>
    /// <param name="attempt">The attempt number, from one.</param>
    public static string AttemptId(string workflowId, string workflowRunId, string activityId, int attempt)
    {
        var text = $"{workflowId}\n{workflowRunId}\n{activityId}\n{attempt}";
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
