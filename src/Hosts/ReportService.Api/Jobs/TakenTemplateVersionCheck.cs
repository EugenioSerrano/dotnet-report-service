using ReportService.TemplatePackages.Records;
using ReportService.TemplatePackages.Versioning;

namespace ReportService.Api.Jobs;

/// <summary>
/// The taken versions a request is judged against at acceptance: whether an exact
/// version already taken may be used by this consumer, and which release version
/// a reference by name falls back to while the template source cannot be
/// checked. The read is one statement against the taken-version store and the
/// visibility is the delivered predicate, so the answer a request receives here
/// and the one the take answers later can never disagree. A version another
/// consumer owns and does not share is indistinguishable from one that does not
/// exist; a reference by name is never refused, because the take decides.
/// </summary>
public sealed class TakenTemplateVersionCheck(ITemplateVersionStore versions)
{
    /// <summary>What the check answered for one reference.</summary>
    /// <param name="Usable">False when the exact version is taken and this consumer may not use it.</param>
    /// <param name="BaselineVersion">The release version a by-name reference falls back to; null when there is none.</param>
    public sealed record Answer(bool Usable, string? BaselineVersion)
    {
        /// <summary>The reference may proceed; a by-name one carries its baseline.</summary>
        /// <param name="baselineVersion">The baseline, when the reference named no version.</param>
        public static Answer Accepted(string? baselineVersion) => new(true, baselineVersion);

        /// <summary>The exact version was taken and this consumer may not use it.</summary>
        public static Answer NotUsable() => new(false, null);
    }

    /// <summary>
    /// Reads the taken versions of <paramref name="templateId"/> and answers the
    /// reference. An exact version that was taken and that this consumer may not use is
    /// refused; a reference by name receives the baseline its acceptance records.
    /// </summary>
    /// <param name="templateId">The template's identifier, already checked against the grammar.</param>
    /// <param name="version">The version the request named, or null for a reference by name.</param>
    /// <param name="consumerId">The declared consumer the version would be taken for.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <exception cref="TemplateStoreUnavailableException">The store could not be read.</exception>
    public async Task<Answer> CheckAsync(
        string templateId,
        string? version,
        string consumerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        ArgumentException.ThrowIfNullOrEmpty(consumerId);

        if (version is not null)
        {
            var record = await versions.FindAsync(templateId, version, cancellationToken).ConfigureAwait(false);
            return record is not null && !TemplateVersionVisibility.MayUse(record, consumerId)
                ? Answer.NotUsable()
                : Answer.Accepted(null);
        }

        var taken = await versions.ListByTemplateAsync(templateId, cancellationToken).ConfigureAwait(false);
        return Answer.Accepted(BaselineOf(taken, consumerId));
    }

    /// <summary>
    /// The highest-precedence usable release version among <paramref name="taken"/>, or null.
    /// Versions of equal precedence are ordered by their full string, as the delivered
    /// resolution orders them, so the baseline never depends on the order of the read.
    /// </summary>
    /// <param name="taken">The records the store holds for one template.</param>
    /// <param name="consumerId">The consumer the baseline is chosen for.</param>
    public static string? BaselineOf(IReadOnlyList<TemplateVersionRecord> taken, string consumerId)
    {
        ArgumentNullException.ThrowIfNull(taken);
        ArgumentException.ThrowIfNullOrEmpty(consumerId);

        string? baseline = null;
        SemanticVersion? highest = null;
        foreach (var record in taken)
        {
            if (!TemplateVersionVisibility.MayUse(record, consumerId)
                || !SemanticVersion.TryParse(record.Version, out var candidate)
                || candidate.PreRelease.Count > 0)
            {
                continue;
            }

            var comparison = highest is null ? 1 : candidate.CompareTo(highest);
            if (comparison > 0
                || (comparison == 0 && string.CompareOrdinal(candidate.Text, baseline) > 0))
            {
                baseline = candidate.Text;
                highest = candidate;
            }
        }

        return baseline;
    }
}
