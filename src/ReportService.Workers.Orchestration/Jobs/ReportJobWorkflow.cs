using Microsoft.Extensions.Logging;
using ReportService.Jobs;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Exceptions;
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

    /// <summary>
    /// The identifier of the patch that gates the two steps this change adds. A run
    /// recorded before it carries no marker for it, so it replays with the patch
    /// inactive and schedules neither step.
    /// </summary>
    public const string JobStatusPathPatch = "job-status-path-v1";

    /// <summary>
    /// The identifier of the patch that gates the job's template step. A run recorded
    /// before it carries no marker for it, so it replays with the patch inactive and
    /// never schedules the step; every run recorded after it carries the step's commands.
    /// </summary>
    public const string TemplateResolvePatch = "template-resolve-v1";

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

        // The job's template is fixed here, once: right after its input is ready and before
        // any step that would need the version. The reference is resolved by the job's own
        // path - a version already taken is bound, one that is not is taken, a name is
        // resolved - and what the answer says is pinned to the job and never read again.
        if (await ResolveTemplateAsync(input.ReportJobId).ConfigureAwait(true))
        {
            return;
        }

        // The first step that takes a usable input to its end: the job's snapshot is read,
        // checked against the template it is pinned to and partitioned before anything is
        // rendered or published, and a refusal ends the job here. A run that has reached
        // this point never continues as a new run: the check count belongs to the wait
        // behind it. The first stage and the finalising step are gated by one patch, so a
        // recorded run that carries no marker for it reproduces its own commands exactly.
        var patched = Workflow.Patched(JobStatusPathPatch);
        if (patched)
        {
            await Workflow.ExecuteActivityAsync(
                (JobTransitionActivities activities) => activities.TransitionJobAsync(
                    new TransitionJobInput(input.ReportJobId, ReportJobStatus.Validating)),
                options).ConfigureAwait(true);
        }

        try
        {
            await Workflow.ExecuteActivityAsync(
                (ValidateAndPartitionActivity activities) => activities.RunAsync(new ValidateAndPartitionInput(input.ReportJobId)),
                PdfOutputActivityOptions.Create()).ConfigureAwait(true);
        }
        catch (ActivityFailureException failure)
            when (failure.InnerException is ApplicationFailureException refusal
                && refusal.NonRetryable
                && !string.IsNullOrEmpty(refusal.ErrorType))
        {
            // The step refused the job's input or could not use its template: the job ends
            // with the code the step named and the diagnostic it carried, through the same
            // helper the wait's own failures use. Any other outcome is left to the engine's
            // own policy, so a transient fault is still retried.
            await FailAsync(
                input.ReportJobId,
                refusal.ErrorType!,
                refusal.Message,
                ReportJobActivityOptions.Create()).ConfigureAwait(true);
            return;
        }

        // The last step of the run: the job is closed from the outcome of its outputs, and
        // a job whose outputs have not ended is left exactly as its steps left it.
        if (patched)
        {
            await Workflow.ExecuteActivityAsync(
                (JobTransitionActivities activities) => activities.FinalizeJobAsync(new FinalizeJobInput(input.ReportJobId)),
                options).ConfigureAwait(true);
        }

        // The point where the steps that follow the data step are added; until they exist
        // the run completes and the job stays as the acceptance left it.
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

    /// <summary>
    /// Fixes the job's template, once, and answers whether the job has ended. A version
    /// already taken is bound without reading the source; one that is not is taken, and a
    /// reference by name is resolved - and when the source cannot be checked, the version
    /// recorded at acceptance is used, never the one the resolution fell back to. Every
    /// refusal of the take ends the job with the code it carries and the text it brought;
    /// any other failure of a step ends it as the step's own failure.
    /// </summary>
    /// <param name="reportJobId">The job whose template is fixed.</param>
    private static async Task<bool> ResolveTemplateAsync(string reportJobId)
    {
        if (!Workflow.Patched(TemplateResolvePatch))
        {
            return false;
        }

        var metadata = TemplateStepActivityOptions.Create();
        try
        {
            var prepared = await Workflow.ExecuteActivityAsync(
                (TemplateResolutionActivities activities) => activities.PrepareTemplateAsync(new PrepareTemplateInput(reportJobId)),
                metadata).ConfigureAwait(true);

            TemplatePinResult? pinned;
            switch (prepared.Outcome)
            {
                case PrepareTemplateOutcome.Terminal:
                    return true;

                case PrepareTemplateOutcome.Pinned:
                    return false;

                case PrepareTemplateOutcome.NeedsTake:
                    var taken = await Workflow.ExecuteActivityAsync(
                        (ITakeTemplateVersionStep step) => step.RunAsync(new TakeTemplateVersionStepInput(
                            prepared.TemplateId!,
                            prepared.RequestedVersion!,
                            prepared.RequestingConsumerId!,
                            reportJobId)),
                        TakeTemplateStepOptions.Create()).ConfigureAwait(true);
                    pinned = await PinTemplateAsync(reportJobId, prepared.TemplateId!, taken.Version, metadata).ConfigureAwait(true);
                    break;

                case PrepareTemplateOutcome.NeedsByName:
                    // The nested try is load-bearing: only the by-name path turns an
                    // unreachable source into the acceptance baseline. A source that cannot be
                    // reached for an exact version is the job's failure, below.
                    try
                    {
                        var resolved = await Workflow.ExecuteActivityAsync(
                            (IResolveTemplateVersionStep step) => step.RunAsync(new ResolveTemplateVersionStepInput(
                                prepared.TemplateId!,
                                prepared.RequestingConsumerId!,
                                reportJobId)),
                            ResolveTemplateStepOptions.Create()).ConfigureAwait(true);
                        pinned = string.Equals(
                                resolved.Outcome,
                                TemplateStepContracts.ResolutionOutcomes.ResolvedFromTakenCopy,
                                StringComparison.Ordinal)
                            ? await PinBaselineAsync(reportJobId, metadata).ConfigureAwait(true)
                            : await PinTemplateAsync(reportJobId, prepared.TemplateId!, resolved.Version, metadata).ConfigureAwait(true);
                    }
                    catch (ActivityFailureException failure) when (TemplateStepFailures.IsSourceUnavailable(failure.InnerException))
                    {
                        pinned = await PinBaselineAsync(reportJobId, metadata).ConfigureAwait(true);
                    }

                    break;

                default:
                    throw new InvalidOperationException("the template step answered an outcome this workflow does not know.");
            }

            return pinned.Outcome is PrepareTemplateOutcome.Terminal;
        }
        catch (ActivityFailureException failure) when (TemplateStepFailures.TryMap(failure.InnerException, out var mapped))
        {
            if (mapped.WasCut(failure.InnerException is ApplicationFailureException application ? application.Message : string.Empty))
            {
                Workflow.Logger.LogWarning(
                    "Job {ReportJobId} template message cut to {Length} characters ({ErrorCode})",
                    reportJobId,
                    TemplateFailureCodes.MessageLimit,
                    mapped.Code);
            }

            await FailAsync(reportJobId, mapped.Code, mapped.Reason, mapped.Message, metadata).ConfigureAwait(true);
            return true;
        }
        catch (ActivityFailureException)
        {
            // Attempts exhausted without a code of their own, or a step that did not answer:
            // the job ends with the code that says the step itself could not finish.
            var mapped = TemplateStepFailures.StepFailed();
            await FailAsync(reportJobId, mapped.Code, mapped.Reason, mapped.Message, metadata).ConfigureAwait(true);
            return true;
        }
    }

    private static Task<TemplatePinResult> PinTemplateAsync(
        string reportJobId,
        string templateId,
        string version,
        ActivityOptions options) =>
        Workflow.ExecuteActivityAsync(
            (TemplateResolutionActivities activities) => activities.PinTemplateAsync(new PinTemplateInput(reportJobId, templateId, version)),
            options);

    private static Task<TemplatePinResult> PinBaselineAsync(string reportJobId, ActivityOptions options) =>
        Workflow.ExecuteActivityAsync(
            (TemplateResolutionActivities activities) => activities.PinBaselineAsync(new PinBaselineInput(reportJobId)),
            options);


    private static Task FailAsync(string reportJobId, string errorCode, string reason, Temporalio.Workflows.ActivityOptions options) =>
        FailAsync(reportJobId, errorCode, reason, null, options);

    private static Task FailAsync(
        string reportJobId,
        string errorCode,
        string reason,
        string? message,
        Temporalio.Workflows.ActivityOptions options) =>
        Workflow.ExecuteActivityAsync(
            (ReportJobActivities activities) => activities.FailJobAsync(new FailJobInput(reportJobId, errorCode, reason, message)),
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
