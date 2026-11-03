using Microsoft.Extensions.Logging;
using ReportService.Jobs;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What the job workflow is asked to run: one report job, by identifier.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
public sealed record ReportJobInput(string ReportJobId);

/// <summary>
/// One report job, as a workflow: it checks the job's data reference and, when that
/// reference cannot be used, ends the job with the condition that made it unusable.
/// While the upload it reports on is still being verified it records the step it is in
/// and waits for it, re-reading the upload on every nudge it is given or on its own
/// timer, whichever comes first. A wait that outlasts the checks one run may take
/// continues as a new run of the same execution, so no run's history grows without
/// limit. It holds no clock, no random value and no I/O of its own, so its history
/// replays exactly — everything that can fail or vary happens inside its activities.
/// </summary>
[Workflow(WorkflowType)]
public sealed class ReportJobWorkflow
{
    /// <summary>The name the workflow is registered and scheduled under.</summary>
    public const string WorkflowType = "ReportJob";

    /// <summary>The queue the workflow itself runs on.</summary>
    public const string OrchestrationQueue = "report-orchestration";

    /// <summary>The prefix of the workflow's identifier: one execution per job.</summary>
    public const string WorkflowIdPrefix = "report-job";

    /// <summary>The name of the nudge that tells a waiting job its upload changed.</summary>
    public const string InputChangedSignal = "input-changed";

    private static TimeSpan _backstop = InputWaitOptions.DefaultBackstop;

    private static int _continueAsNewAfter = InputWaitOptions.DefaultContinueAsNewAfter;

    private int _inputChanges;

    /// <summary>
    /// Publishes the wait's configured bounds to the workflow type. The delivered worker
    /// host instantiates workflows without a service provider, so they are set here, once,
    /// when the process plans its workers — before any execution starts and never per run.
    /// </summary>
    /// <param name="backstop">How long the workflow waits between two checks of its upload.</param>
    /// <param name="continueAsNewAfter">The checks one run may take before it continues as a new run.</param>
    public static void Configure(TimeSpan backstop, int continueAsNewAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(backstop, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(continueAsNewAfter, 1);
        _backstop = backstop;
        _continueAsNewAfter = continueAsNewAfter;
    }

    /// <summary>The wait's bounds as this process published them.</summary>
    /// <returns>The interval between two checks and the checks one run may take.</returns>
    public static (TimeSpan Backstop, int ContinueAsNewAfter) CurrentSettings() => (_backstop, _continueAsNewAfter);

    /// <summary>The identifier of the one execution that runs <paramref name="reportJobId"/>.</summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    public static string WorkflowIdFor(string reportJobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJobId);
        return $"{WorkflowIdPrefix}/{reportJobId}";
    }

    /// <summary>Runs one job up to its continuation point.</summary>
    /// <param name="input">The job to run.</param>
    [WorkflowRun]
    public async Task RunAsync(ReportJobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var options = ReportJobActivityOptions.Create();

        var resolution = await Workflow.ExecuteActivityAsync(
            (ReportJobActivities activities) => activities.ResolveInputAsync(new ResolveInputInput(input.ReportJobId)),
            options).ConfigureAwait(true);

        switch (resolution.Outcome)
        {
            case InputOutcome.Ready:
                break;

            case InputOutcome.Verifying:
                // The job is accepted and not running: it records the step it is in and waits
                // for its upload. Only an upload that became ready brings control to the
                // continuation point; a wait that ended with the job itself, or with the
                // upload rejected or expired, has already ended the job.
                if (!await WaitForInputAsync(input.ReportJobId).ConfigureAwait(true))
                {
                    return;
                }

                break;

            case InputOutcome.Rejected:
                await FailAsync(input.ReportJobId, UploadWireValues.ToStoredText(resolution.RejectedWith()), "input-rejected", options).ConfigureAwait(true);
                return;

            case InputOutcome.Expired:
                await FailAsync(input.ReportJobId, ReportJobActivities.InputNotFound, "input-expired", options).ConfigureAwait(true);
                return;

            case InputOutcome.Terminal:
                return;

            default:
                throw new InvalidOperationException("the input check answered an outcome this workflow does not know.");
        }

        // The point where the steps that take a usable input to its end are added; until
        // they exist the run completes and the job stays as the acceptance left it. A run
        // that has reached this point never continues as a new run: the check count belongs
        // to the wait behind it.
        Workflow.Logger.LogInformation("Job {ReportJobId} input ready", input.ReportJobId);
    }

    /// <summary>
    /// Records that the job waits for its upload, then checks the upload again on every
    /// nudge it receives or on the configured interval, whichever comes first, until the
    /// upload settles, the job ends elsewhere or the run has taken all the checks it may.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <returns>
    /// True when the upload became ready, so the job continues at the point after the wait;
    /// false when the wait ended for any other reason — the upload was rejected or expired,
    /// so the job has failed, or the job itself ended while it waited, so nothing is left to
    /// run and nothing may claim its input is ready.
    /// </returns>
    private async Task<bool> WaitForInputAsync(string reportJobId)
    {
        var options = ReportJobActivityOptions.Create();
        var begun = await Workflow.ExecuteActivityAsync(
            (InputWaitActivities activities) => activities.BeginAsync(new InputWaitInput(reportJobId)),
            InputWaitActivityOptions.Create()).ConfigureAwait(true);
        if (begun == BeginInputWaitOutcome.NotQueued)
        {
            // The job ended while this run was busy; there is nothing to wait for.
            return false;
        }

        // How many checks one wait took, for the operator: the signal says a timer is tuned too
        // long or too short. It is recorded when the wait ends and carries no tag at all, so it
        // can never carry an identifier.
        var performed = Workflow.MetricMeter.CreateHistogram<long>(
            OrchestrationMetrics.InputWaitPassesInstrumentName,
            unit: "{check}",
            description: "Checks one wait for an upload performed.");

        var checks = 0;
        while (true)
        {
            if (checks == _continueAsNewAfter)
            {
                // The only place a run continues as a new one, and it is reached before any
                // step after the wait has been scheduled: the pass count and nothing else.
                Workflow.Logger.LogInformation(
                    "Job {ReportJobId} continued as new after {Checks} checks",
                    reportJobId,
                    checks);
                throw Workflow.CreateContinueAsNewException(
                    (ReportJobWorkflow workflow) => workflow.RunAsync(new ReportJobInput(reportJobId)));
            }

            var resolution = await Workflow.ExecuteActivityAsync(
                (ReportJobActivities activities) => activities.ResolveInputAsync(new ResolveInputInput(reportJobId)),
                options).ConfigureAwait(true);
            checks++;

            switch (resolution.Outcome)
            {
                case InputOutcome.Terminal:
                    // The job ended elsewhere while it waited: the stage it left behind is
                    // cleared, and the run ends here without a step of its own.
                    performed.Record(checks);
                    await EndInputWaitAsync(reportJobId).ConfigureAwait(true);
                    return false;

                case InputOutcome.Ready:
                    performed.Record(checks);
                    await EndInputWaitAsync(reportJobId).ConfigureAwait(true);
                    Workflow.Logger.LogInformation("Job {ReportJobId} input ready after {Checks} checks", reportJobId, checks);
                    return true;

                case InputOutcome.Rejected:
                    performed.Record(checks);
                    await EndInputWaitAsync(reportJobId).ConfigureAwait(true);
                    Workflow.Logger.LogInformation("Job {ReportJobId} input rejected after {Checks} checks", reportJobId, checks);
                    await FailAsync(reportJobId, UploadWireValues.ToStoredText(resolution.RejectedWith()), "input-rejected", options).ConfigureAwait(true);
                    return false;

                case InputOutcome.Expired:
                    performed.Record(checks);
                    await EndInputWaitAsync(reportJobId).ConfigureAwait(true);
                    Workflow.Logger.LogInformation("Job {ReportJobId} input expired after {Checks} checks", reportJobId, checks);
                    await FailAsync(reportJobId, ReportJobActivities.InputNotFound, "input-expired", options).ConfigureAwait(true);
                    return false;

                case InputOutcome.Verifying:
                    // The count is read after the check, never before it: a nudge that arrived
                    // while the upload was being read wakes this wait at once, and one that
                    // arrived before it is already answered by the read.
                    var seen = _inputChanges;
                    await Workflow.WaitConditionAsync(() => _inputChanges != seen, _backstop).ConfigureAwait(true);
                    break;

                default:
                    throw new InvalidOperationException("the input check answered an outcome this workflow does not know.");
            }
        }
    }

    private static Task EndInputWaitAsync(string reportJobId) =>
        Workflow.ExecuteActivityAsync(
            (InputWaitActivities activities) => activities.EndAsync(new InputWaitInput(reportJobId)),
            InputWaitActivityOptions.Create());

    private static Task FailAsync(string reportJobId, string errorCode, string reason, Temporalio.Workflows.ActivityOptions options) =>
        Workflow.ExecuteActivityAsync(
            (ReportJobActivities activities) => activities.FailJobAsync(new FailJobInput(reportJobId, errorCode, reason)),
            options);

    /// <summary>
    /// The nudge that the upload of this job changed. It carries nothing: the workflow
    /// re-reads the upload, so a late, a repeated or a stale nudge cannot change an
    /// outcome by itself, and one that arrives outside the wait is simply counted.
    /// </summary>
    [WorkflowSignal(InputChangedSignal)]
    public Task OnInputChangedAsync()
    {
        _inputChanges++;
        return Task.CompletedTask;
    }
}
