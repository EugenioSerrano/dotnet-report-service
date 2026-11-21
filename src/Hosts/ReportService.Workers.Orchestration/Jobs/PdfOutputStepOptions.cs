using Temporalio.Common;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The execution settings with which this worker schedules the block render on the PDF
/// worker's queue: every value equals the delivered render's own factory. The render
/// keeps a beat, one attempt is bounded, and a block that failed its checks or a package
/// this increment cannot print is never retried - the same data reaches the same verdict.
/// The integration suite asserts every value against the delivered factory.
/// </summary>
public static class RenderPdfBlockStepOptions
{
    /// <summary>The queue the render is served from - the PDF worker's one queue.</summary>
    public const string Queue = "report-pdf-rendering";

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
    /// The codes that are never retried: a document that failed its checks, a package the
    /// increment cannot print, a value beyond the render's guards, and a state the step
    /// cannot use.
    /// </summary>
    public static readonly IReadOnlyList<string> NonRetryableErrorTypes =
    [
        "PAGINATION_FAILED",
        "UNSUPPORTED_CAPABILITY",
        "QUOTA_EXCEEDED",
        "EXECUTION_FAILED",
    ];

    /// <summary>Builds the execution settings of one block render.</summary>
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
}
