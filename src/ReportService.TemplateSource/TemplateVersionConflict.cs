using Microsoft.Extensions.Logging;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource.Source;

namespace ReportService.TemplateSource;

/// <summary>
/// Reports that a taken version reads differently from the copy that was
/// taken. It warns and refuses nothing: the copy and its record stay as they
/// are, every job keeps using them, and the person who changed the publication
/// is asked for a new version. One event carries two causes - two takes that
/// read different bytes, and a version the source now lists with another
/// publication record - and every line names which one it is, so an operator
/// can tell them apart from the log alone.
/// <para>
/// Neither warning carries the bytes of an object, a file's content, or a
/// credential: a key and a hash are provenance, never content.
/// </para>
/// </summary>
public sealed class TemplateVersionConflict
{
    /// <summary>The log category of the template source.</summary>
    public const string LogCategory = "ReportService.TemplateSource";

    /// <summary>The name of the warning's event.</summary>
    public const string EventName = "template.version_conflict";

    /// <summary>The error code the warning carries.</summary>
    public const string Code = "TEMPLATE_VERSION_CONFLICT";

    private readonly ILogger _logger;

    /// <summary>Writes the warning through <paramref name="logger"/>.</summary>
    /// <param name="logger">The log the warning is written to.</param>
    public TemplateVersionConflict(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Reports that <paramref name="taken"/> differs from what the source now holds.</summary>
    /// <param name="taken">The record of the version that was taken.</param>
    /// <param name="readPackageSha256">The package hash of what the source now holds.</param>
    /// <param name="readSourceIdentity">The publication identity the difference was read from.</param>
    /// <param name="reportJobId">The job whose take read the source, when one did.</param>
    public void Report(TemplateVersionRecord taken, string readPackageSha256, string readSourceIdentity, string? reportJobId = null)
    {
        ArgumentNullException.ThrowIfNull(taken);
        ArgumentException.ThrowIfNullOrEmpty(readPackageSha256);
        ArgumentException.ThrowIfNullOrEmpty(readSourceIdentity);

        _logger.LogWarning(
            new EventId(EventIds.VersionConflict, EventName),
            "{Code} template {TemplateId} version {Version} changed in the template source after it was taken; the taken copy is kept. "
            + "Publish the change under a new version. Observation {Observation}, taken package {TakenPackageSha256}, read package {ReadPackageSha256}, read publication {ReadSourceIdentity}, job {ReportJobId}",
            Code,
            taken.TemplateId,
            taken.Version,
            Observations.Race,
            taken.PackageSha256,
            readPackageSha256,
            readSourceIdentity,
            reportJobId ?? "(none)");
    }

    /// <summary>
    /// Observes the publication the source lists for a version that is already
    /// taken. Nothing is written, read or refused: the two identities are compared,
    /// and a difference is one warning carrying both of them.
    /// </summary>
    /// <param name="taken">The record of the version that is already taken.</param>
    /// <param name="listed">The publication the source lists for that version.</param>
    /// <param name="reportJobId">The job the resolution runs for, when there is one.</param>
    /// <returns>
    /// <see langword="true"/> when the listed publication record differs from the
    /// one the version was taken with and the warning was written; otherwise
    /// <see langword="false"/>, and nothing was logged.
    /// </returns>
    public bool ObservePublication(TemplateVersionRecord taken, PublishedVersion listed, string? reportJobId = null)
    {
        ArgumentNullException.ThrowIfNull(taken);
        ArgumentNullException.ThrowIfNull(listed);

        // A version taken before the source was the deployment's object store
        // carries no publication identity: there is no record to compare it with,
        // and comparing one would warn against nothing.
        if (taken.SourceDescriptorSha256 is null
            || string.Equals(taken.SourceDescriptorSha256, listed.DescriptorSha256, StringComparison.Ordinal))
        {
            return false;
        }

        _logger.LogWarning(
            new EventId(EventIds.VersionConflict, EventName),
            "{Code} template {TemplateId} version {Version} is listed in the template source with a publication record other than the one it was taken with; the taken copy is kept. "
            + "Publish the change under a new version. Observation {Observation}, taken publication {TakenSourceDescriptorSha256}, listed publication {ListedDescriptorSha256}, listed record {ListedDescriptorKey}, job {ReportJobId}",
            Code,
            taken.TemplateId,
            taken.Version,
            Observations.PublicationChanged,
            taken.SourceDescriptorSha256,
            listed.DescriptorSha256,
            listed.DescriptorKey.ToString(),
            reportJobId ?? "(none)");
        return true;
    }

    /// <summary>The identifiers of this category's events.</summary>
    public static class EventIds
    {
        /// <summary>A taken version reads differently from its copy.</summary>
        public const int VersionConflict = 4001;
    }

    /// <summary>The causes the one warning reports, as every line names its own.</summary>
    public static class Observations
    {
        /// <summary>Two takes of one version read different bytes; the first record stays.</summary>
        public const string Race = "race";

        /// <summary>The source lists another publication record for a version already taken.</summary>
        public const string PublicationChanged = "publication_changed";
    }
}
