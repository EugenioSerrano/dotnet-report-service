using Temporalio.Common;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Uploads;

/// <summary>
/// The execution settings of one verification attempt, built from configuration
/// and nothing else: the queue it runs on, its two time bounds, its bounded retry
/// policy and the codes the engine must never retry. The factory is pure, so every
/// value is asserted on its own without an engine.
/// </summary>
public static class UploadVerificationActivityOptions
{
    /// <summary>The queue the verification attempts are served from.</summary>
    public const string DataQueue = "report-data";

    /// <summary>How long an attempt may go without a heartbeat before the engine considers it lost.</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The wait after the first failed attempt.</summary>
    public static readonly TimeSpan InitialInterval = TimeSpan.FromSeconds(2);

    /// <summary>How much each further wait grows.</summary>
    public const float BackoffCoefficient = 2f;

    /// <summary>The longest wait between two attempts.</summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(60);

    /// <summary>The most attempts one verification is given.</summary>
    public const int MaximumAttempts = 5;

    /// <summary>
    /// The codes that are never retried: they name a defect of the consumer's data or
    /// of the request, which a later attempt cannot change. Rejections of this work are
    /// results, so this list is a defence in depth rather than the mechanism.
    /// </summary>
    public static readonly IReadOnlyList<string> NonRetryableErrorTypes =
    [
        "DATA_INVALID",
        "RECORD_TOO_LARGE",
        "TEMPLATE_NOT_FOUND",
        "TEMPLATE_VERSION_CONFLICT",
        "TEMPLATE_INVALID",
        "UNSUPPORTED_CAPABILITY",
        "INPUT_NOT_FOUND",
        "EXECUTION_FAILED",
    ];

    /// <summary>Builds the attempt's execution settings.</summary>
    /// <param name="options">The configured time bound of one attempt.</param>
    public static ActivityOptions Create(UploadVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create(options.StartToClose);
    }

    /// <summary>Builds the attempt's execution settings for a bound of <paramref name="startToClose"/>.</summary>
    /// <param name="startToClose">How long one attempt may run.</param>
    public static ActivityOptions Create(TimeSpan startToClose)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(startToClose, TimeSpan.Zero);
        return new ActivityOptions
        {
            TaskQueue = DataQueue,
            StartToCloseTimeout = startToClose,
            HeartbeatTimeout = HeartbeatTimeout,
            RetryPolicy = new RetryPolicy
            {
                InitialInterval = InitialInterval,
                BackoffCoefficient = BackoffCoefficient,
                MaximumInterval = MaximumInterval,
                MaximumAttempts = MaximumAttempts,
                NonRetryableErrorTypes = NonRetryableErrorTypes,
            },
            CancellationType = ActivityCancellationType.WaitCancellationCompleted,
        };
    }
}
