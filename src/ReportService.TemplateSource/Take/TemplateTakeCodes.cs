namespace ReportService.TemplateSource.Take;

/// <summary>
/// The codes a take ends a job with. They are the caller's to branch on, so each
/// one means exactly one condition: the version cannot be read from the source
/// now and may be retried later; the version does not exist, or does not exist
/// for this consumer; the version exists and an author has to fix it; the service
/// itself could not finish a step that is its own to retry.
/// </summary>
public static class TemplateTakeCodes
{
    /// <summary>The source could not be read; the same take may succeed later.</summary>
    public const string TemplateSourceUnavailable = "TEMPLATE_SOURCE_UNAVAILABLE";

    /// <summary>The version does not exist, or does not exist for the consumer asking.</summary>
    public const string TemplateNotFound = "TEMPLATE_NOT_FOUND";

    /// <summary>The version exists and its package or manifest has to be fixed by its author.</summary>
    public const string TemplateInvalid = "TEMPLATE_INVALID";

    /// <summary>The version asks for something this deployment cannot do.</summary>
    public const string UnsupportedCapability = "UNSUPPORTED_CAPABILITY";

    /// <summary>The service could not finish the take for a cause on its own side.</summary>
    public const string ExecutionFailed = "EXECUTION_FAILED";

    /// <summary>A taken version read differently from its copy; the copy is kept.</summary>
    public const string TemplateVersionConflict = "TEMPLATE_VERSION_CONFLICT";
}
