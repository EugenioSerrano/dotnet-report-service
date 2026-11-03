using Temporalio.Common;
using Temporalio.Workflows;

namespace ReportService.Workers.Pdf.Templates;

/// <summary>
/// The execution settings of one take, built from constants and nothing else: the
/// queue it is served from, the beat it must keep, the bound of one attempt, the
/// waits the engine keeps between attempts and the codes it never retries. A
/// caller schedules the activity with these settings; a take that loses its beat
/// is retried from the start of its work, which is why the beat is chosen against
/// the timeout the engine is given.
/// </summary>
public static class TakeTemplateVersionActivityOptions
{
    /// <summary>The queue the take is served from.</summary>
    public const string Queue = "report-pdf-rendering";

    /// <summary>How often a running take reports that it is still working.</summary>
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(5);

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

    /// <summary>A constant summary of the policy, as the recorded execution reference states it.</summary>
    public const string RetryPolicySummary =
        "take: heartbeat 30s, start-to-close 300s, backoff 2s x2 max 60s, 3 attempts";

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
