namespace ReportService.TemplateSource.Resolution;

/// <summary>
/// What one resolution looked at and what it decided, for the log and the
/// metrics only: never returned to a consumer, never carrying a token, a URL, a
/// key, a package path or a manifest value.
/// </summary>
/// <param name="TemplateId">The template's identifier.</param>
/// <param name="SourceDescriptorSha256">The publication record's hash the chosen version was read from, or <see langword="null"/> when the source could not be checked.</param>
/// <param name="SourceVersions">How many names the source enumerated.</param>
/// <param name="TakenVersions">How many records the store holds for the template.</param>
/// <param name="Considered">The considered candidates, newest first.</param>
/// <param name="Skipped">Every candidate the resolution excluded or skipped, with its reason.</param>
/// <param name="Examined">How many considered candidates the walk actually examined.</param>
/// <param name="SourceUnreachable">True when the source or the resolution's deadline cut the walk short.</param>
/// <param name="DeadlineReached">True when the configured deadline was reached before a decision.</param>
public sealed record TemplateResolutionTrace(
    string TemplateId,
    string? SourceDescriptorSha256,
    int SourceVersions,
    int TakenVersions,
    IReadOnlyList<string> Considered,
    IReadOnlyList<CandidateSkip> Skipped,
    int Examined,
    bool SourceUnreachable,
    bool DeadlineReached);
