using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReportService.Jobs;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.TemplatePackages.Records;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The template half of a job's run, as three small metadata steps on the
/// orchestration queue: preparing the reference (a version already taken is bound
/// here, with one read and no source), writing the pin the take or the resolution
/// answered, and writing the baseline a by-name job recorded at acceptance when
/// the source cannot be checked. A pin is written once and the first committed one
/// wins, so a retry, a restart or a newer version taken later never moves a job to
/// another version. Nothing here reads the template source or renders anything: a
/// version that is not taken is the caller's take to fetch, on the PDF worker's
/// queue, and this class only ever writes what that answer said.
/// </summary>
public sealed class TemplateResolutionActivities(
    IJobStore jobs,
    ITemplateVersionStore versions,
    TimeProvider clock,
    OrchestrationMetrics metrics,
    ILoggerFactory loggers,
    ILogger<TemplateResolutionActivities> logger)
{
    /// <summary>The name the preparation is registered and scheduled under.</summary>
    public const string PrepareTemplateName = "PrepareTemplate";

    /// <summary>The name the pin write is registered and scheduled under.</summary>
    public const string PinTemplateName = "PinTemplate";

    /// <summary>The name the baseline write is registered and scheduled under.</summary>
    public const string PinBaselineName = "PinBaseline";

    private static readonly ActivitySource Source = new(TelemetryDefaults.ActivitySourceName);

    /// <summary>
    /// Reads the job's reference and answers what the workflow is to do next: the version
    /// it is already fixed to, that the job ended, or which of the two deliveries to call.
    /// An exact version already taken and usable is pinned here; one taken by another
    /// consumer that does not share it is refused exactly like a version that never
    /// existed.
    /// </summary>
    /// <param name="input">The job to prepare.</param>
    [Activity(PrepareTemplateName)]
    public async Task<PrepareTemplateResult> PrepareTemplateAsync(PrepareTemplateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var context = JobContext.Begin(input.ReportJobId, loggers);
        using var span = Source.StartActivity("template.prepare");
        span?.SetTag(JobContext.ActivityTagName, input.ReportJobId);

        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var reference = await jobs.FindTemplateReferenceAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false);
        if (reference is null)
        {
            // The identifier came from the durable request that started this execution, so a
            // reference it cannot find belongs to a job that no longer exists.
            logger.LogWarning("Job {ReportJobId} is not recorded; there is nothing to resolve", input.ReportJobId);
            return PrepareTemplateResult.Terminal();
        }

        var form = FormOf(reference.ReferenceForm);
        span?.SetTag("template.form", form);
        if (reference.Pin is { } pinned)
        {
            // A pin is never re-resolved: a retry and a restart answer the version on the record.
            LogPinned(input.ReportJobId, pinned, form, TemplatePinPaths.Taken);
            metrics.TemplateResolution(form, TemplatePinPaths.Taken, OrchestrationMetrics.NoCode);
            span?.SetTag("template.path", TemplatePinPaths.Taken);
            return PrepareTemplateResult.Pinned(TemplatePinPaths.Taken);
        }

        var state = await jobs.FindForWorkflowAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false);
        if (state is null || state.Job.IsTerminal)
        {
            return PrepareTemplateResult.Terminal();
        }

        var consumerId = state.Job.ConsumerId;
        if (reference.ReferenceForm is TemplateReferenceForm.ByName)
        {
            return PrepareTemplateResult.NeedsByName(reference.TemplateId, consumerId);
        }

        if (reference.RequestedVersion is not { } requested)
        {
            throw ExecutionFailure(TemplateFailureCodes.RecordMissingMessage);
        }

        var record = await versions.FindAsync(reference.TemplateId, requested, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return PrepareTemplateResult.NeedsTake(reference.TemplateId, requested, consumerId);
        }

        if (!TemplateVersionVisibility.MayUse(record, consumerId))
        {
            // The version was taken by its owner after this job was accepted: the job is ended
            // as if the version did not exist, and nothing of the package is read.
            throw Fail(input.ReportJobId, form, TemplateFailureCodes.NotFound, TemplateFailureCodes.NotFoundMessage, span);
        }

        var pin = await PinAsync(input.ReportJobId, record, form, TemplatePinPaths.Taken, span).ConfigureAwait(false);
        return pin ?? PrepareTemplateResult.Terminal();
    }

    /// <summary>
    /// Writes the version the take or the resolution answered. The record is read again so
    /// the pin carries the values the service stores, never values a caller sent; a version
    /// this consumer may not use, and an exact reference answered with a version it did not
    /// name, are refused rather than pinned.
    /// </summary>
    /// <param name="input">The job, the template and the version to fix it to.</param>
    [Activity(PinTemplateName)]
    public async Task<TemplatePinResult> PinTemplateAsync(PinTemplateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var context = JobContext.Begin(input.ReportJobId, loggers);
        using var span = Source.StartActivity("template.pin");
        span?.SetTag(JobContext.ActivityTagName, input.ReportJobId);

        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var record = await versions.FindAsync(input.TemplateId, input.Version, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            logger.LogError(
                "Job {ReportJobId} named template {TemplateId} {Version}, and no taken version answers it",
                input.ReportJobId,
                input.TemplateId,
                input.Version);
            throw ExecutionFailure(TemplateFailureCodes.RecordMissingMessage);
        }

        var state = await jobs.FindForWorkflowAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false);
        if (state is null || state.Job.IsTerminal)
        {
            return TemplatePinResult.Terminal();
        }

        var reference = await jobs.FindTemplateReferenceAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false);
        var form = reference is null ? OrchestrationMetrics.TemplateFormExact : FormOf(reference.ReferenceForm);
        span?.SetTag("template.form", form);

        if (!TemplateVersionVisibility.MayUse(record, state.Job.ConsumerId))
        {
            // Defence in depth behind the delivered visibility gate of the take.
            throw Fail(input.ReportJobId, form, TemplateFailureCodes.NotFound, TemplateFailureCodes.NotFoundMessage, span);
        }

        if (reference is { ReferenceForm: TemplateReferenceForm.Exact }
            && !string.Equals(reference.RequestedVersion, record.Version, StringComparison.Ordinal))
        {
            // Impossible by construction - an exact reference is only ever taken at the version
            // it named - and refused rather than pinned if it ever happens.
            logger.LogError(
                "Job {ReportJobId} named version {RequestedVersion} and was answered {Version}; nothing is pinned",
                input.ReportJobId,
                reference.RequestedVersion,
                record.Version);
            throw ExecutionFailure(TemplateFailureCodes.StepFailedMessage);
        }

        var pin = await PinAsync(input.ReportJobId, record, form, TemplatePinPaths.Took, span).ConfigureAwait(false);
        return pin is null ? TemplatePinResult.Terminal() : TemplatePinResult.Pinned(pin.Path!);
    }

    /// <summary>
    /// Writes the release version the job recorded at acceptance, for a by-name reference
    /// whose template source cannot be checked. The version taken later by another job is
    /// never used: the baseline is the one this job may fall back to, and a job that has
    /// none is failed with the code that says it may be retried later.
    /// </summary>
    /// <param name="input">The job to fix to its baseline.</param>
    [Activity(PinBaselineName)]
    public async Task<TemplatePinResult> PinBaselineAsync(PinBaselineInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var context = JobContext.Begin(input.ReportJobId, loggers);
        using var span = Source.StartActivity("template.pin");
        span?.SetTag(JobContext.ActivityTagName, input.ReportJobId);

        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var reference = await jobs.FindTemplateReferenceAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false);
        if (reference is null)
        {
            throw ExecutionFailure(TemplateFailureCodes.RecordMissingMessage);
        }

        var form = FormOf(reference.ReferenceForm);
        span?.SetTag("template.form", form);
        if (reference.Pin is { } existing)
        {
            // A pin is never re-resolved, not even by the fallback.
            LogPinned(input.ReportJobId, existing, form, TemplatePinPaths.Taken);
            metrics.TemplateResolution(form, TemplatePinPaths.Taken, OrchestrationMetrics.NoCode);
            return TemplatePinResult.Pinned(TemplatePinPaths.Taken);
        }

        if (reference.BaselineVersion is not { } baseline)
        {
            throw Fail(input.ReportJobId, form, TemplateFailureCodes.SourceUnavailable, TemplateFailureCodes.SourceUnavailableMessage, span);
        }

        logger.LogWarning(
            "Template source could not be checked for job {ReportJobId}; using the version {Version} of {TemplateId} taken when the job was accepted",
            input.ReportJobId,
            baseline,
            reference.TemplateId);

        var record = await versions.FindAsync(reference.TemplateId, baseline, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            logger.LogError(
                "Job {ReportJobId} recorded baseline {Version} of template {TemplateId}, and no taken version answers it",
                input.ReportJobId,
                baseline,
                reference.TemplateId);
            throw ExecutionFailure(TemplateFailureCodes.RecordMissingMessage);
        }

        var pin = await PinAsync(input.ReportJobId, record, form, TemplatePinPaths.Baseline, span).ConfigureAwait(false);
        return pin is null ? TemplatePinResult.Terminal() : TemplatePinResult.Pinned(pin.Path!);
    }

    // The pin write and the log line of every path: the first committed pin is the answer,
    // whatever this attempt read, and a job that ended meanwhile is left as it is. Null is
    // the terminal answer, so a caller can never mistake it for one of the pinned paths.
    private async Task<PrepareTemplateResult?> PinAsync(
        string reportJobId,
        TemplateVersionRecord record,
        string form,
        string path,
        Activity? span)
    {
        var now = clock.GetUtcNow();
        var state = await jobs
            .PinTemplateAsync(reportJobId, TemplatePin.From(record, now), now, ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);
        if (state.JobTerminal)
        {
            return null;
        }

        var pin = state.Pin!;
        LogPinned(reportJobId, pin, form, path);
        metrics.TemplateResolution(form, path, OrchestrationMetrics.NoCode);
        span?.SetTag("template.path", path);
        span?.SetTag("template.version", pin.ResolvedVersion);
        return PrepareTemplateResult.Pinned(path);
    }

    private void LogPinned(string reportJobId, TemplatePin pin, string form, string path) =>
        logger.LogInformation(
            "Job {ReportJobId} template {TemplateId} {Version} pinned ({ReferenceForm}, {Path})",
            reportJobId,
            pin.TemplateId,
            pin.ResolvedVersion,
            form,
            path);

    // The reference form of a reference, for the log line and the counter.
    private static string FormOf(TemplateReferenceForm form) =>
        form is TemplateReferenceForm.Exact ? OrchestrationMetrics.TemplateFormExact : OrchestrationMetrics.TemplateFormByName;

    // A refusal is the job's failure: its code is the failure's type, it is never retried,
    // and its text is a constant of the service - never a value the consumer sent.
    private ApplicationFailureException Fail(string reportJobId, string form, string code, string message, Activity? span)
    {
        logger.LogWarning(
            "Job {ReportJobId} template not usable: {ErrorCode} ({Reason})",
            reportJobId,
            code,
            TemplateFailureCodes.ReasonOf(code, message));
        span?.SetTag("error.code", code);
        metrics.TemplateResolution(form, TemplatePinPaths.Failed, code);
        return new ApplicationFailureException(message, errorType: code, nonRetryable: true);
    }

    private static ApplicationFailureException ExecutionFailure(string message) =>
        new(message, errorType: TemplateFailureCodes.ExecutionFailed, nonRetryable: true);
}
