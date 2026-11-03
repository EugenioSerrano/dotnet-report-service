namespace ReportService.TemplateSource.Resolution;

/// <summary>
/// The codes a by-name resolution can end a job with. A take's own refusals are
/// wider: <c>UNSUPPORTED_CAPABILITY</c> is a candidate's refusal, skipped by the
/// walk, and never a by-name job's code.
/// </summary>
public static class TemplateResolutionCodes
{
    /// <summary>No usable release version of the template exists.</summary>
    public const string TemplateInvalid = "TEMPLATE_INVALID";

    /// <summary>The template, or a version of it this consumer may use, does not exist.</summary>
    public const string TemplateNotFound = "TEMPLATE_NOT_FOUND";

    /// <summary>The template source could not be checked and no release version was already taken.</summary>
    public const string TemplateSourceUnavailable = "TEMPLATE_SOURCE_UNAVAILABLE";
}
