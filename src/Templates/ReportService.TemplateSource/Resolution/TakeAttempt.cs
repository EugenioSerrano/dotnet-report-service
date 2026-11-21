using ReportService.TemplatePackages.Records;

namespace ReportService.TemplateSource.Resolution;

/// <summary>
/// What one call to the take seam answered: a record, or one of the take's own
/// refusals unchanged. The code is the take's (for example
/// <c>TEMPLATE_INVALID</c> or <c>UNSUPPORTED_CAPABILITY</c>) and the reason is
/// what an author has to fix; the resolution keeps both and never rewrites them.
/// </summary>
public abstract record TakeAttempt
{
    private protected TakeAttempt()
    {
    }

    /// <summary>The take produced <paramref name="record"/>.</summary>
    /// <param name="record">The record of the version, as the take wrote it.</param>
    public static TakeAttempt Taken(TemplateVersionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new TakenAttempt(record);
    }

    /// <summary>The take refused the version.</summary>
    /// <param name="code">The take's code, one of <c>TEMPLATE_INVALID</c>, <c>UNSUPPORTED_CAPABILITY</c>, <c>TEMPLATE_NOT_FOUND</c> or <c>TEMPLATE_SOURCE_UNAVAILABLE</c>.</param>
    /// <param name="reason">What the author has to fix.</param>
    /// <param name="retryable">Whether taking the same version again later may succeed.</param>
    public static TakeAttempt Refused(string code, string reason, bool retryable)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new RefusedAttempt(code, reason, retryable);
    }
}

/// <summary>The take produced a record for the version.</summary>
/// <param name="Record">The record it produced.</param>
public sealed record TakenAttempt(TemplateVersionRecord Record) : TakeAttempt;

/// <summary>The take refused the version, with its own code and text.</summary>
/// <param name="Code">The take's code.</param>
/// <param name="Reason">What the author has to fix.</param>
/// <param name="Retryable">Whether taking the same version again later may succeed.</param>
public sealed record RefusedAttempt(string Code, string Reason, bool Retryable) : TakeAttempt;
