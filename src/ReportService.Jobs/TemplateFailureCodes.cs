namespace ReportService.Jobs;

/// <summary>
/// The codes and the fixed reasons a job's template step fails it with, and the
/// texts a consumer reads for each. The codes are the delivered ones the service
/// answers with everywhere (the same code means the same condition, at acceptance
/// and after it); the reasons are the step's own, kept constant so a stored
/// failure carries a value a reader can branch on and never a caller's text.
/// </summary>
public static class TemplateFailureCodes
{
    /// <summary>The template or its version does not exist, or this consumer may not use it.</summary>
    public const string NotFound = "TEMPLATE_NOT_FOUND";

    /// <summary>The template cannot be used as it stands: its author has to fix it.</summary>
    public const string Invalid = "TEMPLATE_INVALID";

    /// <summary>The template source could not be read; the request may be submitted again later.</summary>
    public const string SourceUnavailable = "TEMPLATE_SOURCE_UNAVAILABLE";

    /// <summary>The template's engine is not one this deployment can render.</summary>
    public const string UnsupportedCapability = "UNSUPPORTED_CAPABILITY";

    /// <summary>The step found a record it needs absent, or reached a state it cannot pin.</summary>
    public const string ExecutionFailed = "EXECUTION_FAILED";

    /// <summary>The reason recorded with <see cref="NotFound"/>.</summary>
    public const string NotFoundReason = "template-not-found";

    /// <summary>The reason recorded with <see cref="Invalid"/>.</summary>
    public const string InvalidReason = "template-invalid";

    /// <summary>The reason recorded with <see cref="SourceUnavailable"/>.</summary>
    public const string SourceUnavailableReason = "template-source-unavailable";

    /// <summary>The reason recorded with <see cref="UnsupportedCapability"/>.</summary>
    public const string UnsupportedCapabilityReason = "unsupported-capability";

    /// <summary>The reason recorded when the step itself failed, whatever the cause.</summary>
    public const string StepFailedReason = "template-step-failed";

    /// <summary>The reason recorded when the job's own template record could not be found.</summary>
    public const string RecordMissingReason = "template-record-missing";

    /// <summary>The text of <see cref="NotFound"/>, the same one the acceptance answers with.</summary>
    public const string NotFoundMessage = "The template or template version was not found.";

    /// <summary>The text of <see cref="SourceUnavailable"/>.</summary>
    public const string SourceUnavailableMessage =
        "The template source could not be reached. The request may be submitted again later.";

    /// <summary>The text this service answers with when a job's own template record is missing.</summary>
    public const string RecordMissingMessage = "The job's template record is missing.";

    /// <summary>The text used when the step failed for a cause that carries no code of its own.</summary>
    public const string StepFailedMessage = "The job's template could not be resolved.";

    /// <summary>The longest failure message a job records, in characters.</summary>
    public const int MessageLimit = 1000;

    /// <summary>The codes a failure of the step carries to the job, when its type is one of them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        NotFound,
        Invalid,
        SourceUnavailable,
        UnsupportedCapability,
        ExecutionFailed,
    ];

    /// <summary>True when <paramref name="errorType"/> is a code the step maps to a job failure.</summary>
    /// <param name="errorType">The failure's type, as the activity raised it.</param>
    public static bool IsTemplateCode(string? errorType) =>
        errorType is not null && All.Contains(errorType, StringComparer.Ordinal);

    /// <summary>
    /// The reason recorded beside <paramref name="code"/>. The absent record the step needs
    /// is the one cause that shares its code with the step's own failures, and it is told
    /// apart by the text the service itself raised it with - never by a caller's text.
    /// </summary>
    /// <param name="code">The code of the condition that ended the job.</param>
    /// <param name="message">The text the failure carried.</param>
    public static string ReasonOf(string code, string? message) => (code, message) switch
    {
        (NotFound, _) => NotFoundReason,
        (Invalid, _) => InvalidReason,
        (SourceUnavailable, _) => SourceUnavailableReason,
        (UnsupportedCapability, _) => UnsupportedCapabilityReason,
        (ExecutionFailed, RecordMissingMessage) => RecordMissingReason,
        _ => StepFailedReason,
    };

    /// <summary><paramref name="message"/> cut to <see cref="MessageLimit"/> characters, marked when it was cut.</summary>
    /// <param name="message">The text the failure carried.</param>
    public static string Cap(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Length <= MessageLimit
            ? message
            : string.Concat(message.AsSpan(0, MessageLimit - 3), "...");
    }
}
