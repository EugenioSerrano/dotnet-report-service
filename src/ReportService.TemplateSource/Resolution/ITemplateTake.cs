namespace ReportService.TemplateSource.Resolution;

/// <summary>
/// What the resolution needs from a take: for one version, at one commit, did
/// it become a record or was it refused, and why. The interface is this narrow
/// on purpose — the resolution asks each candidate exactly one question — and
/// the implementation over the real take arrives with it; nothing in this
/// project names that take.
/// </summary>
public interface ITemplateTake
{
    /// <summary>Takes one version on behalf of a consumer, at the resolution's commit.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version to take.</param>
    /// <param name="requestingConsumerId">The consumer the version is taken for.</param>
    /// <param name="commit">The commit the resolution read the candidate set at, or <see langword="null"/> when the caller has none.</param>
    /// <param name="reportJobId">The job on whose behalf the take runs, when there is one.</param>
    /// <param name="cancellationToken">The resolution's deadline, linked to the caller's token.</param>
    /// <returns>The record the take produced, or the take's own refusal.</returns>
    Task<TakeAttempt> TakeAsync(
        string templateId,
        string version,
        string requestingConsumerId,
        SourceCommit? commit,
        string? reportJobId,
        CancellationToken cancellationToken);
}
