using System.Text;
using Temporalio.Common;
using Temporalio.Workflows;

namespace ReportService.Workers.Pdf.Rendering;

/// <summary>
/// The execution settings of one printed block: the queue it is served from, the
/// beat it must keep, the bound of one attempt and the waits the engine keeps
/// between attempts. The settings are built from constants, so they are asserted on
/// their own without an engine; a render that loses its beat is retried from the
/// start of its work, which is safe because the block's object key belongs to the
/// attempt.
/// </summary>
public static class RenderPdfBlockOptions
{
    /// <summary>The queue the render is served from - the PDF worker's one queue.</summary>
    public const string Queue = "report-pdf-rendering";

    /// <summary>How often a running render reports that it is still working.</summary>
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(5);

    /// <summary>How long a render may go without a beat before the engine retries it.</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long one attempt of a render may run.</summary>
    public static readonly TimeSpan StartToClose = TimeSpan.FromSeconds(300);

    /// <summary>The wait after the first failed attempt.</summary>
    public static readonly TimeSpan InitialInterval = TimeSpan.FromSeconds(2);

    /// <summary>How much each further wait grows.</summary>
    public const float BackoffCoefficient = 2f;

    /// <summary>The longest wait between two attempts.</summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(60);

    /// <summary>How many attempts one render gets.</summary>
    public const int MaximumAttempts = 3;

    /// <summary>A constant summary of the policy, as the recorded execution reference states it.</summary>
    public const string RetryPolicySummary = "render: heartbeat 5s/30s, start-to-close 300s, backoff 2s x2 max 60s, 3 attempts";

    /// <summary>
    /// The codes that are never retried: a document that failed its checks, a package
    /// the increment cannot print, a value beyond the render's guards, and a state the
    /// step cannot use.
    /// </summary>
    public static readonly IReadOnlyList<string> NonRetryableErrorTypes =
    [
        "PAGINATION_FAILED",
        "UNSUPPORTED_CAPABILITY",
        "QUOTA_EXCEEDED",
        "EXECUTION_FAILED",
    ];

    /// <summary>Builds the execution settings of one render.</summary>
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
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
