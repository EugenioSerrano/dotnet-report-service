using ReportService.TemplateSource.Take;

namespace ReportService.TemplateSource.Resolution;

/// <summary>
/// The take, seen through the resolution's seam: the only file in the product
/// that names both vocabularies. The take's three answers that carry a record —
/// the take that wrote it, the version another job had already taken, and the
/// version whose bytes differed while it was taken — are one and the same answer
/// to a resolution, because all three hand it a usable record; a refusal keeps
/// its own code, its text and its retryable flag. A failure of the store or the
/// storage is never caught: the take answers the failures it owns, and anything
/// else is the resolution's to fail on.
/// </summary>
public sealed class TemplateTakeAdapter : ITemplateTake
{
    private readonly TemplateTake _take;

    /// <summary>Creates the seam over the take this process owns.</summary>
    /// <param name="take">The take one version is read through.</param>
    public TemplateTakeAdapter(TemplateTake take)
    {
        ArgumentNullException.ThrowIfNull(take);
        _take = take;
    }

    /// <inheritdoc />
    public async Task<TakeAttempt> TakeAsync(
        string templateId,
        string version,
        string requestingConsumerId,
        SourceCommit? commit,
        string? reportJobId,
        CancellationToken cancellationToken)
    {
        var outcome = await _take
            .TakeAsync(templateId, version, requestingConsumerId, commit, reportJobId, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            RefusedVersion refusal => TakeAttempt.Refused(refusal.Code, refusal.Reason, refusal.Retryable),
            VersionOutcome success => TakeAttempt.Taken(success.Version),
            _ => throw new InvalidOperationException("the take answered a state the resolution does not know"),
        };
    }
}
