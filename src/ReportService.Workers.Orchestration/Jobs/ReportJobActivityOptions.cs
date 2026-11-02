using Temporalio.Common;
using Temporalio.Workflows;
using ReportService.Workers.Orchestration.Uploads;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The execution settings shared by the two steps a job runs today, built from
/// constants and nothing else: the queue they run on, the bound of one attempt, the
/// waits the engine keeps between attempts, and the fact that a metadata step is
/// retried for as long as it takes — a database outage makes jobs wait, never fail.
/// The factory is pure, so every value is asserted on its own without an engine.
/// </summary>
public static class ReportJobActivityOptions
{
    /// <summary>The queue the job's steps are served from.</summary>
    public const string Queue = "report-orchestration";

    /// <summary>How long one attempt may run.</summary>
    public static readonly TimeSpan StartToClose = TimeSpan.FromSeconds(30);

    /// <summary>The wait after the first failed attempt.</summary>
    public static readonly TimeSpan InitialInterval = TimeSpan.FromSeconds(1);

    /// <summary>How much each further wait grows.</summary>
    public const float BackoffCoefficient = 2f;

    /// <summary>The longest wait between two attempts.</summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(30);

    /// <summary>No attempt limit: the steps are retried until they succeed.</summary>
    public const int MaximumAttempts = 0;

    /// <summary>A constant summary of the policy, as the recorded execution reference states it.</summary>
    public const string RetryPolicySummary =
        "metadata: start-to-close 30s, backoff 1s x2 max 30s, unbounded";

    /// <summary>
    /// The codes that are never retried. Neither step of a job raises one today — a
    /// refusal of the input is an outcome, not a failure — so the list is the defence
    /// in depth every policy carries, and it is the one the data steps already use.
    /// </summary>
    public static readonly IReadOnlyList<string> NonRetryableErrorTypes = UploadVerificationActivityOptions.NonRetryableErrorTypes;

    /// <summary>Builds the execution settings of one step of a job.</summary>
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
