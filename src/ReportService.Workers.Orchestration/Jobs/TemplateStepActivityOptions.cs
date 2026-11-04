using Temporalio.Common;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The execution settings of the job's template steps. The three metadata steps run on
/// this worker's queue under the unbounded metadata policy: a database outage makes a
/// job wait, never fail. The two activities that read the template source run on the PDF
/// worker's queue, and their settings are the delivered ones mirrored here value for
/// value - the take's Bolt owns its timeouts and retries, and a second policy written in
/// this worker would contradict it. The integration suite asserts every value against the
/// delivered factories.
/// </summary>
public static class TemplateStepActivityOptions
{
    /// <summary>The queue the metadata steps of the template run on.</summary>
    public const string Queue = "report-orchestration";

    /// <summary>How long one metadata attempt may run.</summary>
    public static readonly TimeSpan StartToClose = TimeSpan.FromSeconds(30);

    /// <summary>The wait after the first failed metadata attempt.</summary>
    public static readonly TimeSpan InitialInterval = TimeSpan.FromSeconds(1);

    /// <summary>How much each further wait grows.</summary>
    public const float BackoffCoefficient = 2f;

    /// <summary>The longest wait between two metadata attempts.</summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(30);

    /// <summary>No attempt limit: the metadata steps are retried until they succeed.</summary>
    public const int MaximumAttempts = 0;

    /// <summary>A constant summary of the metadata policy, as the recorded reference states it.</summary>
    public const string RetryPolicySummary =
        "metadata: start-to-close 30s, backoff 1s x2 max 30s, unbounded";

    /// <summary>
    /// The codes the metadata steps are never retried for: the four template codes a job
    /// fails with. A database that is down carries none of them and is retried.
    /// </summary>
    public static readonly IReadOnlyList<string> NonRetryableErrorTypes =
    [
        "TEMPLATE_NOT_FOUND",
        "TEMPLATE_INVALID",
        "TEMPLATE_SOURCE_UNAVAILABLE",
        "UNSUPPORTED_CAPABILITY",
        "EXECUTION_FAILED",
    ];

    /// <summary>Builds the execution settings of one metadata step of the template.</summary>
    public static ActivityOptions Create() => new()
    {
        TaskQueue = Queue,
        StartToCloseTimeout = StartToClose,
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

/// <summary>
/// The execution settings with which this worker schedules the exact take on the PDF
/// worker's queue: every value equals the delivered take's own factory.
/// </summary>
public static class TakeTemplateStepOptions
{
    /// <summary>The queue the take is served from.</summary>
    public const string Queue = "report-pdf-rendering";

    /// <summary>How long a take may go without a beat before the engine retries it.</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long one attempt of a take may run.</summary>
    public static readonly TimeSpan StartToClose = TimeSpan.FromSeconds(300);

    /// <summary>The wait after the first failed attempt.</summary>
    public static readonly TimeSpan InitialInterval = TimeSpan.FromSeconds(2);

    /// <summary>How much each further wait grows.</summary>
    public const float BackoffCoefficient = 2f;

    /// <summary>The longest wait between two attempts.</summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(60);

    /// <summary>How many attempts one take gets, for a source that may come back.</summary>
    public const int MaximumAttempts = 3;

    /// <summary>The codes that are never retried: the version is the author's to fix, or it does not exist.</summary>
    public static readonly IReadOnlyList<string> NonRetryableErrorTypes =
    [
        "TEMPLATE_NOT_FOUND",
        "TEMPLATE_INVALID",
        "UNSUPPORTED_CAPABILITY",
    ];

    /// <summary>Builds the execution settings of one take.</summary>
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

/// <summary>
/// The execution settings with which this worker schedules the by-name resolution on the
/// PDF worker's queue: every value equals the delivered resolution's own factory.
/// </summary>
public static class ResolveTemplateStepOptions
{
    /// <summary>The queue the resolution is served from.</summary>
    public const string Queue = "report-pdf-rendering";

    /// <summary>How long a resolution may go without a beat before the engine retries it.</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long one attempt of a resolution may run.</summary>
    public static readonly TimeSpan StartToClose = TimeSpan.FromSeconds(300);

    /// <summary>The wait after the first failed attempt.</summary>
    public static readonly TimeSpan InitialInterval = TimeSpan.FromSeconds(2);

    /// <summary>How much each further wait grows.</summary>
    public const float BackoffCoefficient = 2f;

    /// <summary>The longest wait between two attempts.</summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(60);

    /// <summary>How many attempts one resolution gets, for a source that may come back.</summary>
    public const int MaximumAttempts = 3;

    /// <summary>The codes that are never retried: no usable release exists, or this consumer may not use it.</summary>
    public static readonly IReadOnlyList<string> NonRetryableErrorTypes =
    [
        "TEMPLATE_NOT_FOUND",
        "TEMPLATE_INVALID",
    ];

    /// <summary>Builds the execution settings of one by-name resolution.</summary>
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
