using Microsoft.Extensions.Logging;
using ReportService.Data;
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

    /// <summary>
    /// The identifier of the patch that gates the job's printing path: the job's move to
    /// running, the lookup of its PDF output and the output's own execution. A run recorded
    /// before it carries no marker for it, so it replays with the patch inactive and
    /// schedules none of the steps — it reaches its finalising step exactly as it did.
    /// </summary>
    public const string PrintingPathPatch = "printing-path-v1";

    /// <summary>
    /// The identifier of the patch that gates the job's multi-output path: starting an output
    /// child per requested output (PDF, HTML, or both) independently, isolating each child's
    /// failure to its own output, and always finalising the job from the per-output states
    /// (ADR-048). A run recorded before it carries no marker for it, so it replays with the patch
    /// inactive and keeps the single-PDF path exactly as it was recorded (ADR-012 §3.1).
    /// </summary>
    public const string HtmlOutputPathPatch = "html-output-path-v1";

    /// <summary>
    /// The identifier of the patch that gates the job's cancellation wind-down: the cancelled
    /// input outcome, the wind-down wrapper that finalises a cancelled run in a non-cancellable
    /// step, and the read a wait performs when its job left the queue. A run recorded before it
    /// carries no marker for it, so it replays with the patch inactive and writes exactly the
    /// commands it recorded (ADR-012 §2.10 V1).
    /// </summary>
    public const string CancellationWindDownPatch = "cancel-wind-down-v1";

    /// <summary>
    /// The identifier of the patch that gates the restart-safe end of a run that fails: the read of
    /// the job's state before the run decides how it ends, and the non-cancellable finalisation a
    /// job that is being cancelled is closed with instead of a failure. A run recorded before it
    /// carries no marker for it, so it replays with the patch inactive and schedules exactly the
    /// failure command it recorded (ADR-012 §2.10 V1).
    /// </summary>
    public const string RestartCancellationGuardPatch = "restart-cancel-guard-v1";

    /// <summary>Runs one job up to its continuation point, winding it down when it is cancelled.</summary>
    /// <param name="input">The job to run.</param>
    [WorkflowRun]
    public async Task RunAsync(ReportJobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Workflow.Patched(CancellationWindDownPatch))
        {
            // A run recorded before the wind-down: its own commands only, exactly as recorded.
            await RunBodyAsync(input, windDown: false).ConfigureAwait(true);
            return;
        }

        try
        {
            await RunBodyAsync(input, windDown: true).ConfigureAwait(true);
        }
        catch (Exception failure) when (TemporalException.IsCanceledException(failure))
        {
            // The cancellation unwound the run: nothing new is scheduled, and the children and
            // activities it started have completed their own cancellation (the long steps wait for
            // it). The job is closed here — in the one step a cancelled run still executes — and
            // the run rethrows, so the execution closes cancelled with the job already terminal
            // (ADR-012 §3.10(3)).
            Workflow.Logger.LogInformation(
                "Job {ReportJobId} cancellation observed; winding the job down",
                input.ReportJobId);
            await FinalizeAfterCancellationAsync(input.ReportJobId).ConfigureAwait(true);
            throw;
        }
    }

    /// <summary>Runs one job up to its continuation point.</summary>
    /// <param name="input">The job to run.</param>
    /// <param name="windDown">Whether the run carries the cancellation wind-down this patch added.</param>
    private async Task RunBodyAsync(ReportJobInput input, bool windDown)
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
                switch (await WaitForInputAsync(input.ReportJobId, windDown).ConfigureAwait(true))
                {
                    case InputWaitOutcome.Ready:
                        break;

                    case InputWaitOutcome.Cancelled:
                        // The cancellation reached the job while it waited: the run closes it
                        // instead of continuing to produce anything.
                        await FinalizeAfterCancellationAsync(input.ReportJobId).ConfigureAwait(true);
                        return;

                    default:
                        return;
                }

                break;

            case InputOutcome.Cancelled:
                // The job was cancelled before this run produced anything: the run closes it and
                // produces nothing (ADR-012 §3.10(2), the cancelled-before-start path).
                await FinalizeAfterCancellationAsync(input.ReportJobId).ConfigureAwait(true);
                return;

            case InputOutcome.Rejected:
                await EndRunAfterFailureAsync(input.ReportJobId, UploadWireValues.ToStoredText(resolution.RejectedWith()), "input-rejected", null, options).ConfigureAwait(true);
                return;

            case InputOutcome.Expired:
                await EndRunAfterFailureAsync(input.ReportJobId, ReportJobActivities.InputNotFound, "input-expired", null, options).ConfigureAwait(true);
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

        PartitionResult partition;
        try
        {
            partition = await Workflow.ExecuteActivityAsync(
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
            await EndRunAfterFailureAsync(
                input.ReportJobId,
                refusal.ErrorType!,
                refusal.Message,
                null,
                ReportJobActivityOptions.Create()).ConfigureAwait(true);
            return;
        }

        // The step that prints what the data step admitted: the job moves to running, the
        // one PDF output it asked for is looked up - a workflow reads nothing itself - and,
        // when the pass refused that output by the row bound, it ends with the reason the
        // pass kept before any child exists for it; otherwise that output is produced by its
        // own execution on the same queue, which prints and verifies the block and publishes
        // it. A failure of any of the steps ends the job with the code the failure carries,
        // so no run stops with the job left running. The whole path is gated by its own
        // patch, so every recorded run replays unchanged.
        if (Workflow.Patched(PrintingPathPatch))
        {
            if (Workflow.Patched(HtmlOutputPathPatch))
            {
                // The multi-output path: an output child per requested output, started in a
                // fixed order, run concurrently, each child's failure isolated to its own output,
                // and the job always finalised from the per-output states (ADR-048, Phase D.0).
                // A whole-job FailJob is never used here — it would mask a sibling's success.
                await RunRequestedOutputsAsync(input, partition, options).ConfigureAwait(true);
            }
            else
            {
                // The recorded single-PDF path (runs predating html-output-path-v1): kept
                // byte-for-byte so their histories replay unchanged.
                try
                {
                    await Workflow.ExecuteActivityAsync(
                        (JobTransitionActivities activities) => activities.TransitionJobAsync(
                            new TransitionJobInput(input.ReportJobId, ReportJobStatus.Running)),
                        options).ConfigureAwait(true);

                    var output = await Workflow.ExecuteActivityAsync(
                        (PdfOutputActivities activities) => activities.FindPdfOutputAsync(new FindPdfOutputInput(input.ReportJobId)),
                        options).ConfigureAwait(true);

                    if (partition.PdfRefusal is { } refusal)
                    {
                        // The data step refused this output: it ends here, with the output's code
                        // and the reason the pass kept, and no child is started for it.
                        await Workflow.ExecuteActivityAsync(
                            (JobTransitionActivities activities) => activities.FailOutputAsync(
                                new FailOutputInput(output.OutputId, refusal.Code, refusal.Reason)),
                            options).ConfigureAwait(true);
                    }
                    else
                    {
                        await Workflow.ExecuteChildWorkflowAsync(
                            (ReportPdfOutputWorkflow workflow) => workflow.RunAsync(
                                new ReportPdfOutputInput(input.ReportJobId, output.OutputId)),
                            new ChildWorkflowOptions
                            {
                                Id = ReportPdfOutputWorkflow.WorkflowIdFor(input.ReportJobId),
                                ParentClosePolicy = ParentClosePolicy.RequestCancel,
                            }).ConfigureAwait(true);
                    }
                }
                catch (ActivityFailureException failure)
                {
                    await EndRunAfterFailureAsync(
                        input.ReportJobId,
                        ReportPdfOutputWorkflow.CodeOf(failure.InnerException),
                        "printing-failed",
                        MessageOf(failure.InnerException),
                        options).ConfigureAwait(true);
                    return;
                }
                catch (ChildWorkflowFailureException failure)
                {
                    await EndRunAfterFailureAsync(
                        input.ReportJobId,
                        ReportPdfOutputWorkflow.CodeOf(failure.InnerException),
                        "printing-failed",
                        MessageOf(failure.InnerException),
                        options).ConfigureAwait(true);
                    return;
                }
            }
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
    /// Runs the job's requested outputs, each as its own child, under the multi-output path
    /// (Phase D.0, ADR-048). The children are started in a fixed order — PDF when requested, then
    /// HTML when requested — captured without awaiting so they run concurrently, and each child's
    /// failure is isolated to its own output through <c>FailOutput</c>. A whole-job fail is never
    /// used here: a mixed outcome must reach <c>FinalizeJob</c>, which rolls up the job's terminal
    /// status from the per-output states. The job's move to running happens once, before any
    /// child.
    /// </summary>
    /// <param name="input">The job to run.</param>
    /// <param name="partition">The partition result, carrying any row-bound PDF refusal.</param>
    /// <param name="options">The activity options for the metadata steps.</param>
    private static async Task RunRequestedOutputsAsync(
        ReportJobInput input,
        PartitionResult partition,
        ActivityOptions options)
    {
        await Workflow.ExecuteActivityAsync(
            (JobTransitionActivities activities) => activities.TransitionJobAsync(
                new TransitionJobInput(input.ReportJobId, ReportJobStatus.Running)),
            options).ConfigureAwait(true);

        // A workflow reads nothing itself: the per-format lookup is nullable and never fails when
        // a format is absent, so a job may request only HTML, only PDF, or both without the other
        // format's lookup failing it.
        var pdf = await Workflow.ExecuteActivityAsync(
            (PdfOutputActivities activities) => activities.FindOutputAsync(
                new FindOutputInput(input.ReportJobId, OutputFormat.Pdf)),
            options).ConfigureAwait(true);
        var html = await Workflow.ExecuteActivityAsync(
            (PdfOutputActivities activities) => activities.FindOutputAsync(
                new FindOutputInput(input.ReportJobId, OutputFormat.Html)),
            options).ConfigureAwait(true);

        var children = new List<Task>();

        // PDF first (fixed, data-independent order for replay determinism, ADR-012 §3.1).
        if (pdf.OutputId is { } pdfOutputId)
        {
            if (partition.PdfRefusal is { } refusal)
            {
                // The data step refused the PDF output: it ends here with no child started, as it
                // did on the single-PDF path.
                await Workflow.ExecuteActivityAsync(
                    (JobTransitionActivities activities) => activities.FailOutputAsync(
                        new FailOutputInput(pdfOutputId, refusal.Code, refusal.Reason)),
                    options).ConfigureAwait(true);
            }
            else
            {
                var pdfChild = Workflow.ExecuteChildWorkflowAsync(
                    (ReportPdfOutputWorkflow workflow) => workflow.RunAsync(
                        new ReportPdfOutputInput(input.ReportJobId, pdfOutputId)),
                    new ChildWorkflowOptions
                    {
                        Id = ReportPdfOutputWorkflow.WorkflowIdFor(input.ReportJobId),
                        ParentClosePolicy = ParentClosePolicy.RequestCancel,
                        // The dependency is recorded at the call site: a cancelled job waits for
                        // its child's cleanup before it finalises (ADR-012 §3.10(3)). It is the
                        // SDK's default, stated so it cannot be lost silently.
                        CancellationType = ChildWorkflowCancellationType.WaitCancellationCompleted,
                    });
                children.Add(IsolateOutputAsync(pdfChild, pdfOutputId, ReportPdfOutputWorkflow.CodeOf, options));
            }
        }

        // HTML next. The HTML capability refusal lives in the assembly activity (AC-5), so the
        // HTML child is always started when the job requests it — there is no partition-time
        // HTML refusal.
        if (html.OutputId is { } htmlOutputId)
        {
            var htmlChild = Workflow.ExecuteChildWorkflowAsync(
                (ReportHtmlOutputWorkflow workflow) => workflow.RunAsync(
                    new ReportHtmlOutputInput(input.ReportJobId, htmlOutputId)),
                new ChildWorkflowOptions
                {
                    Id = ReportHtmlOutputWorkflow.WorkflowIdFor(input.ReportJobId),
                    ParentClosePolicy = ParentClosePolicy.RequestCancel,
                    CancellationType = ChildWorkflowCancellationType.WaitCancellationCompleted,
                });
            children.Add(IsolateOutputAsync(htmlChild, htmlOutputId, ReportHtmlOutputWorkflow.CodeOf, options));
        }

        if (children.Count > 0)
        {
            await Workflow.WhenAllAsync(children).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Awaits one output child, isolating an unexpected fault to that output: a child that faults
    /// the parent (a <see cref="ChildWorkflowFailureException"/>, or an
    /// <see cref="ActivityFailureException"/>) ends only its own output through <c>FailOutput</c>;
    /// the sibling continues and the job is never whole-job-failed, so <c>FinalizeJob</c> rolls up
    /// from the per-output states (ADR-048). A child that ended its own output normally throws
    /// nothing, so nothing is done here for it.
    /// </summary>
    /// <param name="child">The child execution's task.</param>
    /// <param name="outputId">The output the child produces.</param>
    /// <param name="codeOf">The output's own code mapping for an unexpected fault.</param>
    /// <param name="options">The activity options for the metadata step.</param>
    private static async Task IsolateOutputAsync(
        Task child,
        string outputId,
        Func<Exception?, string> codeOf,
        ActivityOptions options)
    {
        // The await and the FailOutput command are the only workflow-visible steps; the
        // isolation verdict itself is the pure DecideOutputIsolation, kept here so a
        // hermetic unit test can exercise it without a Temporal worker. An unexpected
        // child fault is captured, the verdict is computed, and only then — never between
        // two awaits that would reorder commands — is the one output failed. A fault the
        // filter does not match propagates exactly as before.
        Exception? childFailure = null;
        try
        {
            await child.ConfigureAwait(true);
        }
        catch (Exception failure) when (failure is ChildWorkflowFailureException or ActivityFailureException)
        {
            childFailure = failure;
        }

        var decision = DecideOutputIsolation(outputId, childFailure, codeOf);
        if (decision.ShouldFailOutput)
        {
            // A verdict that fails an output always carries its code (DecideOutputIsolation
            // maps it through codeOf); only a verdict that fails nothing carries a null code.
            await Workflow.ExecuteActivityAsync(
                (JobTransitionActivities activities) => activities.FailOutputAsync(
                    new FailOutputInput(decision.OutputId, decision.Code!, null)),
                options).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The per-output isolation verdict, as a pure function of the output and the child's
    /// outcome: an unexpected child fault (a <see cref="ChildWorkflowFailureException"/> or an
    /// <see cref="ActivityFailureException"/>) fails that one output through <c>FailOutput</c>
    /// with the output's own code mapping and never fails the whole job, so a sibling's success
    /// still reaches <c>FinalizeJob</c>; any other outcome (a child that ended its own output
    /// normally, so no fault) fails nothing. It holds no clock, no I/O and no workflow call, so
    /// a hermetic unit test exercises the whole decision without a Temporal worker.
    /// </summary>
    /// <param name="outputId">The output the child produces.</param>
    /// <param name="childFailure">The fault the child raised, or <see langword="null"/> when it ended normally.</param>
    /// <param name="codeOf">The output's own code mapping for an unexpected fault.</param>
    /// <returns>The verdict: whether to fail the output, which output, and with what code.</returns>
    public static OutputIsolationDecision DecideOutputIsolation(
        string outputId,
        Exception? childFailure,
        Func<Exception?, string> codeOf)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        ArgumentNullException.ThrowIfNull(codeOf);

        return childFailure is ChildWorkflowFailureException or ActivityFailureException
            ? new OutputIsolationDecision(true, outputId, codeOf(childFailure.InnerException))
            : new OutputIsolationDecision(false, outputId, null);
    }

    /// <summary>
    /// Records that the job waits for its upload, then checks the upload again on every
    /// nudge it receives or on the configured interval, whichever comes first, until the
    /// upload settles, the job ends elsewhere or the run has taken all the checks it may.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="windDown">Whether the run carries the cancellation wind-down this patch added.</param>
    /// <returns>
    /// <see cref="InputWaitOutcome.Ready"/> when the upload became ready, so the job continues at
    /// the point after the wait; <see cref="InputWaitOutcome.Cancelled"/> when the job is being
    /// cancelled, so the run closes it and produces nothing; <see cref="InputWaitOutcome.Ended"/>
    /// when the wait ended for any other reason — the upload was rejected or expired, so the job
    /// has failed, or the job itself ended while it waited, so nothing is left to run.
    /// </returns>
    private async Task<InputWaitOutcome> WaitForInputAsync(string reportJobId, bool windDown)
    {
        var options = ReportJobActivityOptions.Create();
        var begun = await Workflow.ExecuteActivityAsync(
            (InputWaitActivities activities) => activities.BeginAsync(new InputWaitInput(reportJobId)),
            InputWaitActivityOptions.Create()).ConfigureAwait(true);
        if (begun == BeginInputWaitOutcome.NotQueued)
        {
            // The job left the queue while this run was busy. Without the wind-down there is
            // nothing to wait for and the run ends; with it, a job that is being cancelled is
            // closed here instead of being left silently behind.
            if (!windDown)
            {
                return InputWaitOutcome.Ended;
            }

            var current = await Workflow.ExecuteActivityAsync(
                (ReportJobActivities activities) => activities.ResolveInputAsync(new ResolveInputInput(reportJobId)),
                options).ConfigureAwait(true);
            return current.Outcome == InputOutcome.Cancelled
                ? InputWaitOutcome.Cancelled
                : InputWaitOutcome.Ended;
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
                    return InputWaitOutcome.Ended;

                case InputOutcome.Cancelled:
                    // The job is being cancelled: the stage it left behind is cleared and the run
                    // closes the job in its non-cancellable finalisation.
                    performed.Record(checks);
                    await EndInputWaitAsync(reportJobId).ConfigureAwait(true);
                    Workflow.Logger.LogInformation("Job {ReportJobId} cancelled after {Checks} checks", reportJobId, checks);
                    return InputWaitOutcome.Cancelled;

                case InputOutcome.Ready:
                    performed.Record(checks);
                    await EndInputWaitAsync(reportJobId).ConfigureAwait(true);
                    Workflow.Logger.LogInformation("Job {ReportJobId} input ready after {Checks} checks", reportJobId, checks);
                    return InputWaitOutcome.Ready;

                case InputOutcome.Rejected:
                    performed.Record(checks);
                    await EndInputWaitAsync(reportJobId).ConfigureAwait(true);
                    Workflow.Logger.LogInformation("Job {ReportJobId} input rejected after {Checks} checks", reportJobId, checks);
                    await EndRunAfterFailureAsync(reportJobId, UploadWireValues.ToStoredText(resolution.RejectedWith()), "input-rejected", null, options).ConfigureAwait(true);
                    return InputWaitOutcome.Ended;

                case InputOutcome.Expired:
                    performed.Record(checks);
                    await EndInputWaitAsync(reportJobId).ConfigureAwait(true);
                    Workflow.Logger.LogInformation("Job {ReportJobId} input expired after {Checks} checks", reportJobId, checks);
                    await EndRunAfterFailureAsync(reportJobId, ReportJobActivities.InputNotFound, "input-expired", null, options).ConfigureAwait(true);
                    return InputWaitOutcome.Ended;

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
    /// Closes a cancelled job, in the one step a cancelled run still executes: the finalisation
    /// is scheduled with its own non-cancellable token, so the request that cancelled the run
    /// cannot cancel the closure that writes the terminal status (ADR-012 §3.10(3)). A
    /// finalisation that keeps failing is retried by the metadata policy and never discarded: the
    /// run stays open and the job non-terminal until the store answers, which is the intended
    /// "a database outage makes jobs wait, never fail" behaviour.
    /// </summary>
    /// <param name="reportJobId">The job to close.</param>
    private static async Task FinalizeAfterCancellationAsync(string reportJobId)
    {
        var result = await Workflow.ExecuteActivityAsync(
            (JobTransitionActivities activities) => activities.FinalizeJobAsync(new FinalizeJobInput(reportJobId)),
            ReportJobActivityOptions.CreateFinalization()).ConfigureAwait(true);

        Workflow.Logger.LogInformation(
            "Job {ReportJobId} cancellation finalisation: {Outcome} ({Status})",
            reportJobId,
            result.Outcome,
            result.Status is { } status ? JobWireValues.ToStoredText(status) : OrchestrationMetrics.NoCode);
    }

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

            await EndRunAfterFailureAsync(reportJobId, mapped.Code, mapped.Reason, mapped.Message, metadata).ConfigureAwait(true);
            return true;
        }
        catch (ActivityFailureException)
        {
            // Attempts exhausted without a code of their own, or a step that did not answer:
            // the job ends with the code that says the step itself could not finish.
            var mapped = TemplateStepFailures.StepFailed();
            await EndRunAfterFailureAsync(reportJobId, mapped.Code, mapped.Reason, mapped.Message, metadata).ConfigureAwait(true);
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


    /// <summary>
    /// The message a job carries for a failure: the product's own text when the failure is
    /// one of its refusals, and nothing for a failure the product did not write - an
    /// engine or transport message is not a consumer's diagnostic.
    /// </summary>
    /// <param name="failure">The failure the engine reported inside the step's or the child's failure.</param>
    /// <returns>The message, or <see langword="null"/> when the failure carries none of the product's own.</returns>
    public static string? MessageOf(Exception? failure) =>
        failure is ApplicationFailureException application && !string.IsNullOrEmpty(application.Message)
            ? application.Message
            : null;

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
    /// Ends a run that failed, after asking the job's own durable record how it ends. A run
    /// recorded before the guard's patch wrote the failure exactly as it always did, and this
    /// method writes it exactly as it was recorded. A run recorded after it reads the job's state
    /// first — in the non-cancellable scope, because on a cancelled workflow the default token
    /// would cancel the very read that decides the outcome — and then:
    /// <list type="bullet">
    /// <item>a job that is being cancelled is closed through the non-cancellable finalisation, the
    /// one writer of the terminal <c>CANCELLED</c> (ADR-048 R0), and the failure it ended with is
    /// not written — a cancelled job carries no error code;</item>
    /// <item>a job that is already terminal is left exactly as it is: there is nothing to write,
    /// and the run ends;</item>
    /// <item>any other state is the ordinary failure this run always wrote, with the same code,
    /// reason, message and command.</item>
    /// </list>
    /// Reading first, not after the write, keeps the guarantee independent of the write's own
    /// guard: the decision is made on the durable state and never on what a guarded update would
    /// have answered.
    /// </summary>
    /// <param name="reportJobId">The job to end.</param>
    /// <param name="errorCode">The code of the condition that ended it.</param>
    /// <param name="reason">The fixed reason recorded beside the code.</param>
    /// <param name="message">The message the job carries, when the failing step has one.</param>
    /// <param name="options">The activity options of the failure command.</param>
    private static async Task EndRunAfterFailureAsync(
        string reportJobId,
        string errorCode,
        string reason,
        string? message,
        Temporalio.Workflows.ActivityOptions options)
    {
        if (!Workflow.Patched(RestartCancellationGuardPatch))
        {
            await FailAsync(reportJobId, errorCode, reason, message, options).ConfigureAwait(true);
            return;
        }

        var snapshot = await Workflow.ExecuteActivityAsync(
            (JobTransitionActivities activities) => activities.ReadJobStateAsync(new ReadJobStateInput(reportJobId)),
            ReportJobActivityOptions.CreateFinalization()).ConfigureAwait(true);

        var decision = DecideEndAfterFailure(snapshot.Status);
        if (decision == JobEndDecision.CloseAsCancelled)
        {
            Workflow.Logger.LogInformation(
                "Job {ReportJobId} ended {Status}: the run failed while the job was being cancelled",
                reportJobId,
                JobWireValues.ToStoredText(ReportJobStatus.Cancelled));
            await FinalizeAfterCancellationAsync(reportJobId).ConfigureAwait(true);
            return;
        }

        if (decision == JobEndDecision.Stop)
        {
            return;
        }

        // The ordinary failure, exactly as a run recorded before this guard wrote it.
        await FailAsync(reportJobId, errorCode, reason, message, options).ConfigureAwait(true);
    }

    /// <summary>
    /// How a run that failed ends, as a pure function of the job's durable state and nothing else:
    /// a job that is being cancelled is closed through the non-cancellable finalisation
    /// (<see cref="JobEndDecision.CloseAsCancelled"/>, ADR-048 R0); a job that already reached a
    /// terminal state is left untouched (<see cref="JobEndDecision.Stop"/>); every other state is
    /// the failure this run always wrote (<see cref="JobEndDecision.FailAsToday"/>). Every state
    /// the enumeration declares is named, and a value it does not declare is refused rather than
    /// silently failed: the design fails closed. A state added to the enumeration later must be
    /// decided here — the hermetic table test enumerates the enumeration, so an undecided state
    /// fails that test instead of taking the ordinary path. It holds no clock, no I/O and no
    /// workflow call, so a hermetic unit test exercises it.
    /// </summary>
    /// <param name="status">The state the job's record carried when the run failed.</param>
    /// <returns>How the run ends.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a state the enumeration declares.</exception>
    public static JobEndDecision DecideEndAfterFailure(ReportJobStatus status) => status switch
    {
        ReportJobStatus.Cancelling => JobEndDecision.CloseAsCancelled,
        ReportJobStatus.Cancelled => JobEndDecision.Stop,
        ReportJobStatus.Succeeded => JobEndDecision.Stop,
        ReportJobStatus.Failed => JobEndDecision.Stop,
        ReportJobStatus.PartiallySucceeded => JobEndDecision.Stop,
        ReportJobStatus.Queued => JobEndDecision.FailAsToday,
        ReportJobStatus.Validating => JobEndDecision.FailAsToday,
        ReportJobStatus.Partitioning => JobEndDecision.FailAsToday,
        ReportJobStatus.Running => JobEndDecision.FailAsToday,
        ReportJobStatus.Finalizing => JobEndDecision.FailAsToday,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "the job state is not one the status enumeration declares."),
    };

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

/// <summary>
/// How a job's wait for its upload ended: <see cref="Ready"/> when the upload became usable,
/// <see cref="Cancelled"/> when the job is being cancelled and must be closed instead of
/// continuing, and <see cref="Ended"/> when the wait ended any other way — the upload was
/// rejected or expired, or the job itself ended while it waited.
/// </summary>
public enum InputWaitOutcome
{
    /// <summary>The upload became ready: the job continues after the wait.</summary>
    Ready,

    /// <summary>The wait ended with the job ended, or with its upload unusable: nothing follows it.</summary>
    Ended,

    /// <summary>The job is being cancelled: its run closes it and produces nothing.</summary>
    Cancelled,
}

/// <summary>
/// The verdict of <see cref="ReportJobWorkflow.DecideOutputIsolation"/>: whether one output is
/// failed in isolation, which output, and with what code. A verdict that fails no output carries
/// a null <see cref="Code"/>; the job is never failed as a whole by this verdict.
/// </summary>
/// <param name="ShouldFailOutput">Whether the output is failed through <c>FailOutput</c>.</param>
/// <param name="OutputId">The output the verdict concerns.</param>
/// <param name="Code">The code the output is failed with, or <see langword="null"/> when none is.</param>
public readonly record struct OutputIsolationDecision(bool ShouldFailOutput, string OutputId, string? Code);

/// <summary>
/// How a run that ended by a failure ends its job, as <see cref="ReportJobWorkflow.DecideEndAfterFailure"/>
/// answers it: closed as cancelled through the non-cancellable finalisation, stopped because the
/// job already ended, or failed exactly as the run always failed it.
/// </summary>
public enum JobEndDecision
{
    /// <summary>The job is being cancelled: the run closes it through the non-cancellable finalisation.</summary>
    CloseAsCancelled,

    /// <summary>The job already reached a terminal state: nothing is written and the run ends.</summary>
    Stop,

    /// <summary>The job is neither cancelled nor terminal: the run writes its failure as it always did.</summary>
    FailAsToday,
}
