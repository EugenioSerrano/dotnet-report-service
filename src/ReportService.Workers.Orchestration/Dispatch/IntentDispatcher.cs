using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Workers.Orchestration.Jobs;
using ReportService.Workers.Orchestration.Telemetry;
using ReportService.Workers.Orchestration.Uploads;

namespace ReportService.Workers.Orchestration.Dispatch;

/// <summary>
/// Turns the durable requests the API records into workflows. Each poll claims a
/// bounded batch of the requests that are due and, for each in request order, asks
/// the engine whether the execution already exists before starting one — so a
/// process that died between a start and its record, or a second process polling
/// beside this one, still produces exactly one execution per request. A request is
/// never failed: a failed attempt is recorded with its wait and tried again, and one
/// that waits too long is reported instead. The dispatcher never exits on an engine
/// or database failure, and it never keeps a client: a connection the host replaced
/// is used from the next call.
/// </summary>
public sealed class IntentDispatcher : BackgroundService
{
    /// <summary>The wait after the first failed attempt.</summary>
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(2);

    /// <summary>The longest wait between two attempts.</summary>
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(60);

    private readonly IIntentStore _store;
    private readonly IWorkflowStarter _starter;
    private readonly TemporalClientHolder _holder;
    private readonly DispatcherOptions _options;
    private readonly UploadVerificationOptions _verification;
    private readonly TimeProvider _clock;
    private readonly OrchestrationMetrics _metrics;
    private readonly ILogger<IntentDispatcher> _logger;
    private readonly HashSet<long> _reported = [];
    private readonly Lock _gate = new();

    /// <summary>Creates the dispatcher over the request store, the engine seam and the process's clock.</summary>
    /// <param name="store">The durable requests.</param>
    /// <param name="starter">The engine operations a dispatch needs.</param>
    /// <param name="holder">The process's current engine connection.</param>
    /// <param name="options">The polling settings, as the process validated them.</param>
    /// <param name="verification">The settings the dispatched work runs under, summarized in its reference, as the process validated them.</param>
    /// <param name="clock">The clock every wait, age and budget is measured with.</param>
    /// <param name="metrics">The process's instruments.</param>
    /// <param name="logger">The log, which carries identifiers and codes only.</param>
    public IntentDispatcher(
        IIntentStore store,
        IWorkflowStarter starter,
        TemporalClientHolder holder,
        IOptions<DispatcherOptions> options,
        IOptions<UploadVerificationOptions> verification,
        TimeProvider clock,
        OrchestrationMetrics metrics,
        ILogger<IntentDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(starter);
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _starter = starter;
        _holder = holder;
        _options = options.Value;
        _verification = verification.Value;
        _clock = clock;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>The wait after the attempt that failed <paramref name="previousAttempts"/> times before it: 2 s, doubling, at most 60 s.</summary>
    /// <param name="previousAttempts">How many attempts were recorded before the failed one.</param>
    public static TimeSpan BackoffDelay(int previousAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(previousAttempts);
        var wait = FirstBackoff;
        for (var attempt = 0; attempt < previousAttempts && wait < MaximumBackoff; attempt++)
        {
            wait *= 2;
        }

        return wait < MaximumBackoff ? wait : MaximumBackoff;
    }

    /// <summary>A constant summary of the policy the dispatched verification runs under.</summary>
    /// <param name="startToClose">The configured bound of one attempt.</param>
    public static string RetryPolicySummary(TimeSpan startToClose) =>
        $"data: heartbeat {UploadVerificationActivityOptions.HeartbeatTimeout.TotalSeconds:0}s, "
        + $"start-to-close {startToClose.TotalSeconds:0}s, "
        + $"backoff {UploadVerificationActivityOptions.InitialInterval.TotalSeconds:0}s x{UploadVerificationActivityOptions.BackoffCoefficient:0} "
        + $"max {UploadVerificationActivityOptions.MaximumInterval.TotalSeconds:0}s, "
        + $"{UploadVerificationActivityOptions.MaximumAttempts} attempts";

    /// <summary>A constant summary of the policy the dispatched request's work runs under.</summary>
    /// <param name="kind">The request's kind, a fixed value.</param>
    /// <param name="verificationStartToClose">The configured bound of one verification attempt.</param>
    public static string RetryPolicySummaryFor(string kind, TimeSpan verificationStartToClose) => kind switch
    {
        IntentKinds.StartJob => ReportJobActivityOptions.RetryPolicySummary,
        _ => RetryPolicySummary(verificationStartToClose),
    };

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A database or engine failure never ends the dispatcher; the next poll tries again.
                _logger.LogWarning("A dispatch poll failed ({ExceptionType})", exception.GetType().Name);
            }

            try
            {
                await Task.Delay(_options.PollInterval, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One poll: claims what is due, dispatches it within the budget, then reports what waits too long.</summary>
    /// <param name="cancellationToken">The poll's cancellation token.</param>
    public async Task DispatchDueAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        await using var claim = await _store.ClaimDueAsync(now, _options.BatchSize, cancellationToken).ConfigureAwait(false);
        var started = _clock.GetTimestamp();
        foreach (var intent in claim.Intents)
        {
            if (_clock.GetElapsedTime(started) >= _options.ClaimBudget)
            {
                // The budget bounds how long one poll holds its rows; the rest is left for the next.
                _logger.LogWarning(
                    "The dispatch budget of one poll elapsed; {Requests} requests were left for the next poll",
                    claim.Intents.Count);
                break;
            }

            await DispatchAsync(claim, intent, now, cancellationToken).ConfigureAwait(false);
            ReportIfLate(intent, now);
        }

        await claim.CommitAsync(cancellationToken).ConfigureAwait(false);
        await ObservePendingAgeAsync(now, cancellationToken).ConfigureAwait(false);
    }

    private async Task DispatchAsync(IIntentClaim claim, IntentRecord intent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (PlanFor(intent) is not { } plan)
        {
            // A kind this process does not serve is never dispatched and never failed.
            _logger.LogError("Intent {IntentId} has a kind this process does not serve", intent.IntentId);
            return;
        }

        var workflowId = plan.WorkflowId;
        try
        {
            if (_holder.Current is null)
            {
                // No connection yet: this attempt failed for a cause on the engine's side.
                throw new InvalidOperationException("no engine connection is current.");
            }

            // Describe before starting: an execution that already exists serves the request,
            // whether it is still running or already closed.
            var served = await _starter.DescribeAsync(workflowId, cancellationToken).ConfigureAwait(false);
            if (served is null)
            {
                try
                {
                    served = await plan.Start(workflowId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A start that collides with another process's is served by that execution.
                    served = await _starter.DescribeAsync(workflowId, cancellationToken).ConfigureAwait(false);
                    if (served is null)
                    {
                        throw new InvalidOperationException("the workflow could not be started.", exception);
                    }
                }
            }

            await claim.MarkDispatchedAsync(
                intent.IntentId,
                new ExecutionReference(
                    served.WorkflowId,
                    served.RunId,
                    ConfigurationValidation.ProductVersion(),
                    RetryPolicySummaryFor(intent.Kind, _verification.StartToClose),
                    now),
                now,
                cancellationToken).ConfigureAwait(false);

            _metrics.DispatchAttempt(intent.Kind, OrchestrationMetrics.DispatchedResult);
            _logger.LogInformation("Intent {IntentId} ({Kind}) dispatched as {WorkflowId}", intent.IntentId, intent.Kind, served.WorkflowId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var wait = BackoffDelay(intent.Attempts);
            await claim.RecordFailureAsync(intent.IntentId, now, now + wait, cancellationToken).ConfigureAwait(false);
            _metrics.DispatchAttempt(intent.Kind, OrchestrationMetrics.FailedResult);
            _logger.LogWarning(
                "Intent {IntentId} ({Kind}) could not be dispatched ({ExceptionType}); next attempt in {WaitSeconds} s",
                intent.IntentId,
                intent.Kind,
                exception.GetType().Name,
                wait.TotalSeconds);
        }
    }

    // What a request of this kind is turned into: the identifier its execution must have, and
    // the start that produces it. A kind this process does not serve has no plan.
    private DispatchPlan? PlanFor(IntentRecord intent) => intent.Kind switch
    {
        IntentKinds.StartUploadVerification => new DispatchPlan(
            UploadVerificationWorkflow.WorkflowIdFor(intent.SubjectId),
            (workflowId, cancellationToken) => _starter.StartUploadVerificationAsync(workflowId, intent.SubjectId, cancellationToken)),
        IntentKinds.StartJob => new DispatchPlan(
            ReportJobWorkflow.WorkflowIdFor(intent.SubjectId),
            (workflowId, cancellationToken) => _starter.StartReportJobAsync(workflowId, intent.SubjectId, cancellationToken)),
        _ => null,
    };

    // The two things a plan needs: the identifier an execution must have, and the start
    // that asks the engine for it.
    private readonly record struct DispatchPlan(
        string WorkflowId,
        Func<string, CancellationToken, Task<WorkflowStart>> Start);

    // One warning per waiting request and process, so a stuck request is visible once and not once per poll.
    private void ReportIfLate(IntentRecord intent, DateTimeOffset now)
    {
        if (now - intent.CreatedAt < _options.AlertAfter)
        {
            return;
        }

        bool first;
        lock (_gate)
        {
            first = _reported.Add(intent.IntentId);
        }

        if (first)
        {
            _logger.LogWarning(
                "Intent {IntentId} ({Kind}) pending for more than {AlertAfter}",
                intent.IntentId,
                intent.Kind,
                _options.AlertAfter.ToString());
        }
    }

    private async Task ObservePendingAgeAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var age = await _store.OldestPendingAgeAsync(now, cancellationToken).ConfigureAwait(false);
        _metrics.ObservePendingAge(IntentKinds.StartUploadVerification, age?.TotalSeconds ?? 0);
    }
}
