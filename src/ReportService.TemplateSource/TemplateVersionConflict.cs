using Microsoft.Extensions.Logging;
using ReportService.TemplatePackages.Records;

namespace ReportService.TemplateSource;

/// <summary>
/// Reports that a taken version reads differently from the copy that was
/// taken. It warns and refuses nothing: the copy and its record stay as they
/// are, every job keeps using them, and the person who changed the branch is
/// asked for a new version. The warning carries both package hashes, so the
/// difference is provable from the log alone.
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
    /// <param name="readCommitSha">The commit the difference was read at.</param>
    /// <param name="reportJobId">The job whose take read the source, when one did.</param>
    public void Report(TemplateVersionRecord taken, string readPackageSha256, string readCommitSha, string? reportJobId = null)
    {
        ArgumentNullException.ThrowIfNull(taken);
        ArgumentException.ThrowIfNullOrEmpty(readPackageSha256);
        ArgumentException.ThrowIfNullOrEmpty(readCommitSha);

        _logger.LogWarning(
            new EventId(EventIds.VersionConflict, EventName),
            "{Code} template {TemplateId} version {Version} changed in the template source after it was taken; the taken copy is kept. "
            + "Publish the change under a new version. Taken package {TakenPackageSha256}, read package {ReadPackageSha256}, read commit {ReadCommitSha}, job {ReportJobId}",
            Code,
            taken.TemplateId,
            taken.Version,
            taken.PackageSha256,
            readPackageSha256,
            readCommitSha,
            reportJobId ?? "(none)");
    }

    /// <summary>The identifiers of this category's events.</summary>
    public static class EventIds
    {
        /// <summary>A taken version reads differently from its copy.</summary>
        public const int VersionConflict = 4001;
    }
}
