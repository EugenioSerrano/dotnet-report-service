using Temporalio.Common;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The execution settings with which the HTML output workflow schedules the assembly on
/// its own <c>report-html</c> task queue — never the PDF worker's <c>report-pdf-rendering</c>
/// queue (AC-4), so an HTML-only job schedules no work there and no browser is started. One
/// attempt is bounded by a start-to-close with a heartbeat sized for a streaming assembly, and
/// the same deterministic-refusal discipline as the PDF path applies: a package this increment
/// cannot produce HTML, an invalid template or data, a value beyond the guards, and a state the
/// step cannot use are never retried — the same input reaches the same verdict.
/// </summary>
public static class AssembleHtmlStepOptions
{
    /// <summary>The queue the HTML assembly is served from — the orchestration worker's HTML queue.</summary>
    public const string Queue = "report-html";

    /// <summary>How long an assembly may go without a beat before the engine retries it.</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long one attempt of an assembly may run.</summary>
    public static readonly TimeSpan StartToClose = TimeSpan.FromSeconds(120);

    /// <summary>The wait after the first failed attempt.</summary>
    public static readonly TimeSpan InitialInterval = TimeSpan.FromSeconds(2);

    /// <summary>How much each further wait grows.</summary>
    public const float BackoffCoefficient = 2f;

    /// <summary>The longest wait between two attempts.</summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(60);

    /// <summary>How many attempts one assembly gets.</summary>
    public const int MaximumAttempts = 3;

    /// <summary>
    /// The codes that are never retried: a package this increment cannot produce HTML from, an
    /// invalid template or data, a value beyond the guards, and a state the step cannot use.
    /// </summary>
    public static readonly IReadOnlyList<string> NonRetryableErrorTypes =
    [
        "UNSUPPORTED_CAPABILITY",
        "TEMPLATE_INVALID",
        "DATA_INVALID",
        "QUOTA_EXCEEDED",
        "EXECUTION_FAILED",
    ];

    /// <summary>Builds the execution settings of one HTML assembly.</summary>
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
