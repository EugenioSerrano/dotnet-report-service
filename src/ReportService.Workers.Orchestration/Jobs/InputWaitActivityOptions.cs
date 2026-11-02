using Temporalio.Common;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The execution settings of the two steps that record and clear a job's waiting
/// stage, built from constants and nothing else: the orchestration queue, the bound of
/// one attempt, the waits the engine keeps between attempts, and the fact that these
/// steps are retried for as long as it takes — a database that is briefly unreachable
/// makes a job wait longer, never fail. The factory is pure, so every value is
/// asserted on its own without an engine.
/// </summary>
public static class InputWaitActivityOptions
{
    /// <summary>The queue these steps are served from.</summary>
    public const string Queue = ReportJobActivityOptions.Queue;

    /// <summary>How long one attempt may run.</summary>
    public static readonly TimeSpan StartToClose = ReportJobActivityOptions.StartToClose;

    /// <summary>The wait after the first failed attempt.</summary>
    public static readonly TimeSpan InitialInterval = ReportJobActivityOptions.InitialInterval;

    /// <summary>How much each further wait grows.</summary>
    public const float BackoffCoefficient = ReportJobActivityOptions.BackoffCoefficient;

    /// <summary>The longest wait between two attempts.</summary>
    public static readonly TimeSpan MaximumInterval = ReportJobActivityOptions.MaximumInterval;

    /// <summary>No attempt limit: the steps are retried until they succeed.</summary>
    public const int MaximumAttempts = ReportJobActivityOptions.MaximumAttempts;

    /// <summary>Builds the execution settings of one of the two steps.</summary>
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
        },
    };
}
