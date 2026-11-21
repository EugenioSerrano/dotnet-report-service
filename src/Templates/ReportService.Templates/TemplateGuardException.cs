namespace ReportService.Templates;

/// <summary>The error codes a refusal of this assembly carries.</summary>
public static class TemplateErrorCodes
{
    /// <summary>The composed text passed a guard value.</summary>
    public const string QuotaExceeded = "QUOTA_EXCEEDED";

    /// <summary>The composed text was not produced within its deadline.</summary>
    public const string RenderTimeout = "RENDER_TIMEOUT";

    /// <summary>The package declares an engine or a capability this build does not compose.</summary>
    public const string UnsupportedCapability = "UNSUPPORTED_CAPABILITY";
}

/// <summary>
/// A render that a guard stopped: the composed HTML passed its size, or the
/// deadline was reached. Both are deterministic refusals of the composition, not
/// of the printing.
/// </summary>
/// <param name="Code">The error code the refusal answers with.</param>
/// <param name="Reason">Why the render was stopped, in words that carry no data value.</param>
public sealed class TemplateGuardException(string code, string reason) : Exception(reason)
{
    /// <summary>The error code the refusal answers with.</summary>
    public string Code { get; } = code;

    /// <summary>The declared limit the render passed, when one applies.</summary>
    public string? Limit { get; init; }

    /// <summary>The refusal with a declared limit.</summary>
    public static TemplateGuardException Quota(string limit, string reason) =>
        new(TemplateErrorCodes.QuotaExceeded, reason) { Limit = limit };

    /// <summary>The refusal for a render that passed its deadline.</summary>
    public static TemplateGuardException Timeout(string reason) => new(TemplateErrorCodes.RenderTimeout, reason);
}
