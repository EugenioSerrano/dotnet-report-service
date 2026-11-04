using Temporalio.Common;
using Temporalio.Workflows;

namespace ReportService.Workers.Pdf.Templates;

/// <summary>
/// The execution settings of one by-name resolution, built from constants and
/// nothing else: the queue it is served from, the beat it must keep, the bound of
/// one attempt, the waits the engine keeps between attempts and the codes it
/// never retries. Only the two refusals that are deterministic for a given state
/// of the branch are never retried; a source that may come back is, and it keeps
/// its own code once the attempts are spent.
/// </summary>
public static class TemplateResolutionActivityOptions
{
    /// <summary>The queue the resolution is served from — the PDF worker's one queue.</summary>
    public const string Queue = "report-pdf-rendering";

    /// <summary>How often a running resolution reports that it is still working.</summary>
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(5);

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

    /// <summary>A constant summary of the policy, as the recorded execution reference states it.</summary>
    public const string RetryPolicySummary =
        "resolve: heartbeat 5s/30s, start-to-close 300s, backoff 2s x2 max 60s, 3 attempts";

    /// <summary>
    /// The codes that are never retried: no usable release version exists, or the
    /// template is not one this consumer may use. An unsupported capability is a
    /// candidate's refusal, skipped by the walk, and never a by-name job's code.
    /// </summary>
    public static readonly IReadOnlyList<string> NonRetryableErrorTypes =
    [
        "TEMPLATE_NOT_FOUND",
        "TEMPLATE_INVALID",
    ];

    /// <summary>Builds the execution settings of one resolution.</summary>
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
