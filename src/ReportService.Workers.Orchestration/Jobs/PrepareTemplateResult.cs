namespace ReportService.Workers.Orchestration.Jobs;
/// <summary>What a job's template step is asked to prepare: the job, by identifier.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
public sealed record PrepareTemplateInput(string ReportJobId);

/// <summary>What writing a job's pin is asked to do: fix one version of one template on it.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="TemplateId">The template the job named.</param>
/// <param name="Version">The version the take or the resolution answered.</param>
public sealed record PinTemplateInput(string ReportJobId, string TemplateId, string Version);

/// <summary>What writing a by-name job's baseline is asked to do: fix the version recorded at acceptance.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
public sealed record PinBaselineInput(string ReportJobId);

/// <summary>The paths a job's template was fixed by, as the log line and the counter carry them.</summary>
public static class TemplatePinPaths
{
    /// <summary>An exact version was already taken and usable: it was bound without a take.</summary>
    public const string Taken = "taken";

    /// <summary>A take or a by-name resolution ran and the version it answered was pinned.</summary>
    public const string Took = "took";

    /// <summary>The template source could not be checked, so the acceptance baseline was pinned.</summary>
    public const string Baseline = "baseline";

    /// <summary>The step failed the job.</summary>
    public const string Failed = "failed";
}

/// <summary>How the preparation of a job's template ended, as the workflow is told it.</summary>
public enum PrepareTemplateOutcome
{
    /// <summary>The job is fixed to a version; nothing else is to be done.</summary>
    Pinned,

    /// <summary>The job had already ended: there is nothing to resolve.</summary>
    Terminal,

    /// <summary>The exact version is not taken: the workflow is to call the take for it.</summary>
    NeedsTake,

    /// <summary>The reference named the template only: the workflow is to resolve its version.</summary>
    NeedsByName,
}

/// <summary>
/// What preparing a job's template answered: that it is pinned (and by which path),
/// that the job had ended, or which of the two deliveries the workflow is to call -
/// with the template, the version it must request and the consumer it is taken for.
/// </summary>
/// <param name="Outcome">How the preparation ended.</param>
/// <param name="TemplateId">The template the job named, when the workflow has to act.</param>
/// <param name="RequestedVersion">The exact version to take, when one was named.</param>
/// <param name="RequestingConsumerId">The consumer the version is taken for.</param>
/// <param name="Path">How the job was pinned, when it is.</param>
public sealed record PrepareTemplateResult(
    PrepareTemplateOutcome Outcome,
    string? TemplateId = null,
    string? RequestedVersion = null,
    string? RequestingConsumerId = null,
    string? Path = null)
{
    /// <summary>The job is fixed to a version, by <paramref name="path"/>.</summary>
    /// <param name="path">One of <see cref="TemplatePinPaths"/>.</param>
    public static PrepareTemplateResult Pinned(string path) => new(PrepareTemplateOutcome.Pinned, Path: path);

    /// <summary>The job had already ended.</summary>
    public static PrepareTemplateResult Terminal() => new(PrepareTemplateOutcome.Terminal);

    /// <summary>The exact version must be taken for <paramref name="requestingConsumerId"/>.</summary>
    /// <param name="templateId">The template the job named.</param>
    /// <param name="version">The exact version it named.</param>
    /// <param name="requestingConsumerId">The consumer the version is taken for.</param>
    public static PrepareTemplateResult NeedsTake(string templateId, string version, string requestingConsumerId) =>
        new(PrepareTemplateOutcome.NeedsTake, templateId, version, requestingConsumerId);

    /// <summary>The version of <paramref name="templateId"/> must be resolved for <paramref name="requestingConsumerId"/>.</summary>
    /// <param name="templateId">The template the job named.</param>
    /// <param name="requestingConsumerId">The consumer the version is chosen for.</param>
    public static PrepareTemplateResult NeedsByName(string templateId, string requestingConsumerId) =>
        new(PrepareTemplateOutcome.NeedsByName, templateId, RequestingConsumerId: requestingConsumerId);
}

/// <summary>What writing a pin answered: the job is fixed by <paramref name="path"/>, or it had ended.</summary>
/// <param name="Outcome">How the write ended.</param>
/// <param name="Path">How the job was pinned, when it is.</param>
public sealed record TemplatePinResult(PrepareTemplateOutcome Outcome, string? Path = null)
{
    /// <summary>The job is fixed to a version, by <paramref name="path"/>.</summary>
    /// <param name="path">One of <see cref="TemplatePinPaths"/>.</param>
    public static TemplatePinResult Pinned(string path) => new(PrepareTemplateOutcome.Pinned, path);

    /// <summary>The job had already ended.</summary>
    public static TemplatePinResult Terminal() => new(PrepareTemplateOutcome.Terminal);
}

