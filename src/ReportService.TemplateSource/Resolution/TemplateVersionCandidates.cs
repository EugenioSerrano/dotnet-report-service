using ReportService.TemplatePackages.Records;
using ReportService.TemplatePackages.Versioning;

namespace ReportService.TemplateSource.Resolution;

/// <summary>The fixed reasons a candidate is excluded or skipped, as the log records them.</summary>
public static class CandidateSkipReasons
{
    /// <summary>The name is not a semantic version, so it can never be a version.</summary>
    public const string NotSemVer = "not-semver";

    /// <summary>The version is a pre-release, which a by-name reference never chooses.</summary>
    public const string PreRelease = "pre-release";

    /// <summary>The requesting consumer may not use the taken version.</summary>
    public const string NotVisible = "not-visible";

    /// <summary>The take refused the version; it is skipped like an invalid one.</summary>
    public const string Refused = "refused";
}

/// <summary>One candidate the resolution did not walk, and why.</summary>
/// <param name="Version">The version string, as the source or the store wrote it.</param>
/// <param name="Reason">One of <see cref="CandidateSkipReasons"/>.</param>
/// <param name="Code">For a refusal, the take's code; otherwise <see langword="null"/>.</param>
/// <param name="RefusalReason">For a refusal, what the take said to fix; otherwise <see langword="null"/>.</param>
public sealed record CandidateSkip(string Version, string Reason, string? Code, string? RefusalReason);

/// <summary>One considered candidate: its version, its parsed precedence, and its record when it is already taken.</summary>
/// <param name="Version">The version string.</param>
/// <param name="Semantic">The parsed version, whose precedence the order uses.</param>
/// <param name="Taken">The record when the version is already taken; otherwise <see langword="null"/>.</param>
public sealed record TemplateVersionCandidate(string Version, SemanticVersion Semantic, TemplateVersionRecord? Taken);

/// <summary>
/// The candidate set of one resolution and its order. The set is the union of
/// the version names the source listed at the commit and the versions the store
/// already holds, de-duplicated by ordinal string equality. A name that is not
/// a semantic version, a pre-release, and a taken version the consumer may not
/// use are excluded with their reason. The rest are ordered newest first by
/// precedence; when two versions have equal precedence, the one already taken
/// comes first, and when neither or both were taken the higher full version
/// string in ordinal order comes first. Build metadata orders nothing beyond
/// that string.
/// </summary>
public sealed class TemplateVersionCandidates
{
    private static readonly IComparer<TemplateVersionCandidate> NewestFirst = Comparer<TemplateVersionCandidate>.Create(Compare);

    private TemplateVersionCandidates(
        IReadOnlyList<TemplateVersionCandidate> considered,
        IReadOnlyList<CandidateSkip> skipped)
    {
        Considered = considered;
        Skipped = skipped;
    }

    /// <summary>The considered candidates, newest first.</summary>
    public IReadOnlyList<TemplateVersionCandidate> Considered { get; }

    /// <summary>Every candidate that was excluded, with its reason.</summary>
    public IReadOnlyList<CandidateSkip> Skipped { get; }

    /// <summary>Builds the candidate set of one resolution.</summary>
    /// <param name="sourceVersions">The names the source listed at the commit, in the order it answered them.</param>
    /// <param name="taken">The records the store holds for the template.</param>
    /// <param name="requestingConsumerId">The consumer the resolution runs for.</param>
    public static TemplateVersionCandidates Build(
        IReadOnlyList<string> sourceVersions,
        IReadOnlyList<TemplateVersionRecord> taken,
        string requestingConsumerId)
    {
        ArgumentNullException.ThrowIfNull(sourceVersions);
        ArgumentNullException.ThrowIfNull(taken);
        ArgumentException.ThrowIfNullOrEmpty(requestingConsumerId);

        var takenByVersion = new Dictionary<string, TemplateVersionRecord>(StringComparer.Ordinal);
        foreach (var record in taken)
        {
            takenByVersion[record.Version] = record;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var considered = new List<TemplateVersionCandidate>();
        var skipped = new List<CandidateSkip>();

        foreach (var version in sourceVersions)
        {
            if (seen.Add(version))
            {
                Consider(version, takenByVersion.GetValueOrDefault(version), requestingConsumerId, considered, skipped);
            }
        }

        foreach (var record in taken)
        {
            if (seen.Add(record.Version))
            {
                Consider(record.Version, record, requestingConsumerId, considered, skipped);
            }
        }

        considered.Sort(NewestFirst);
        return new TemplateVersionCandidates(considered, skipped);
    }

    private static void Consider(
        string version,
        TemplateVersionRecord? taken,
        string requestingConsumerId,
        List<TemplateVersionCandidate> considered,
        List<CandidateSkip> skipped)
    {
        if (!SemanticVersion.TryParse(version, out var semantic))
        {
            skipped.Add(new CandidateSkip(version, CandidateSkipReasons.NotSemVer, null, null));
            return;
        }

        if (semantic.PreRelease.Count > 0)
        {
            skipped.Add(new CandidateSkip(version, CandidateSkipReasons.PreRelease, null, null));
            return;
        }

        if (taken is not null && !TemplateVersionVisibility.MayUse(taken, requestingConsumerId))
        {
            skipped.Add(new CandidateSkip(version, CandidateSkipReasons.NotVisible, null, null));
            return;
        }

        considered.Add(new TemplateVersionCandidate(version, semantic, taken));
    }

    private static int Compare(TemplateVersionCandidate left, TemplateVersionCandidate right)
    {
        var compared = right.Semantic.CompareTo(left.Semantic);
        if (compared != 0)
        {
            return compared;
        }

        var leftTaken = left.Taken is not null;
        var rightTaken = right.Taken is not null;
        if (leftTaken != rightTaken)
        {
            return leftTaken ? -1 : 1;
        }

        return string.CompareOrdinal(right.Version, left.Version);
    }
}
