using ReportService.TemplatePackages.Records;

namespace ReportService.TemplateSource.Resolution;

/// <summary>
/// The answer of one by-name resolution: either the record the job will use, or
/// one typed refusal with the code and the text the job ends with. The set of
/// codes is narrower than a take's on purpose — <c>UNSUPPORTED_CAPABILITY</c> is
/// a candidate's refusal and never a job's code.
/// </summary>
public abstract record TemplateResolutionOutcome
{
    private protected TemplateResolutionOutcome()
    {
    }

    /// <summary>The resolution chose <paramref name="record"/>.</summary>
    /// <param name="record">The record of the version the job uses.</param>
    /// <param name="fromTakenCopy">True when the source could not be checked and the newest release already taken was used.</param>
    public static TemplateResolutionOutcome Resolved(TemplateVersionRecord record, bool fromTakenCopy)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new ResolvedOutcome(record, fromTakenCopy);
    }

    /// <summary>The resolution refused the template.</summary>
    /// <param name="code">One of <c>TEMPLATE_INVALID</c>, <c>TEMPLATE_NOT_FOUND</c> or <c>TEMPLATE_SOURCE_UNAVAILABLE</c>.</param>
    /// <param name="reason">What the consumer is told, and what an author has to fix.</param>
    /// <param name="retryable">Whether the same job may succeed later.</param>
    public static TemplateResolutionOutcome Refused(string code, string reason, bool retryable)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new RefusedOutcome(code, reason, retryable);
    }
}

/// <summary>The resolution chose a version.</summary>
/// <param name="Record">The record of the chosen version.</param>
/// <param name="FromTakenCopy">True when it came from the newest release already taken because the source could not be checked.</param>
public sealed record ResolvedOutcome(TemplateVersionRecord Record, bool FromTakenCopy) : TemplateResolutionOutcome;

/// <summary>The resolution refused the template with a job-level code.</summary>
/// <param name="Code">The job's code.</param>
/// <param name="Reason">What the consumer is told.</param>
/// <param name="Retryable">Whether the same job may succeed later.</param>
public sealed record RefusedOutcome(string Code, string Reason, bool Retryable) : TemplateResolutionOutcome;
