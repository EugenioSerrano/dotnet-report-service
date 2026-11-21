using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ReportService.Storage;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Versioning;

namespace ReportService.TemplateSource.Source;

/// <summary>
/// The deployment's template source, read through one object store: it finds a
/// version's publication record, opens it verified against the hash in its own
/// name and the size the listing gave, checks it against its own rules, and reads
/// every file only against the size and SHA-256 the record declares. It is read
/// and list only - a read never writes, deletes or repairs - and it resolves no
/// configuration of its own. Absent, incomplete, duplicate, unreadable and invalid
/// stay apart: only a class that cannot be listed or read is unreachable.
/// </summary>
public sealed partial class StorageTemplateSource : ITemplateSourceReader
{
    /// <summary>The log category of the template source.</summary>
    public const string LogCategory = "ReportService.TemplateSource";

    /// <summary>The constant text of an unavailable class; the same version may be retried later.</summary>
    public const string UnavailableReason = "The template source could not be read. The version may be retried later.";

    private const string ListingDisagreesReason = "The listing and the template source class disagree about the publication record. The version may be retried later.";

    private readonly IObjectStore _store;
    private readonly TemplateSourceOptions _options;
    private readonly TemplateSourceMetrics _metrics;
    private readonly ILogger _logger;

    /// <summary>Creates the read over the store the deployment gives the class.</summary>
    /// <param name="store">The one store of the class.</param>
    /// <param name="options">The deployment's caps.</param>
    /// <param name="metrics">The read's instruments.</param>
    /// <param name="logger">The read's log.</param>
    public StorageTemplateSource(
        IObjectStore store,
        TemplateSourceOptions options,
        TemplateSourceMetrics metrics,
        ILogger<StorageTemplateSource> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _options = options;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SourceVersionRead> ReadVersionAsync(string templateId, string version, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = TemplateSourceMetrics.ActivitySource.StartActivity("template.source.read");
        activity?.SetTag("template.id", Printable(templateId));

        // A name that cannot be a version of a template is answered as absent,
        // before any store call. The take answers not-found for it already.
        if (!TemplateIdentifier().IsMatch(templateId) || !SemanticVersionSyntax.IsValid(version))
        {
            return Finish(new SourceVersionRead.Absent(), templateId, version, started);
        }

        var read = await ReadCoreAsync(templateId, version, cancellationToken).ConfigureAwait(false);
        return Finish(read, templateId, version, started);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PublishedVersion>> ListPublishedVersionsAsync(string templateId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        var prefix = TemplateSourceKeys.PublishedPrefix(templateId);
        var results = new List<PublishedVersion>();

        try
        {
            await foreach (var info in _store.ListAsync(prefix, cancellationToken).ConfigureAwait(false))
            {
                if (!TemplateSourceKeys.TryReadPublishedRecord(info.Key, templateId, out var versionSegment, out var sha256, out var reason))
                {
                    Skip(info.Key, reason);
                    continue;
                }

                var text = versionSegment;
                if (!KeySegmentEncoding.IsRaw(versionSegment))
                {
                    // A hashed segment costs one verified record read to learn its version text.
                    text = await ReadVersionTextAsync(new SourceDescriptorName(info.Key, sha256, info.Size), cancellationToken)
                        .ConfigureAwait(false);
                    if (text is null)
                    {
                        Skip(info.Key, "the record yields no version text this service reads");
                        continue;
                    }
                }

                if (!SemanticVersionSyntax.IsValid(text))
                {
                    Skip(info.Key, "the version text is not a semantic version");
                    continue;
                }

                results.Add(new PublishedVersion(text, info.Key, sha256, info.Size));
            }
        }
        catch (StorageException)
        {
            throw new TemplateSourceException(TemplateSourceFailure.Unreachable, UnavailableReason);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task OpenFileAsync(
        string templateId,
        string version,
        SourceFile file,
        Func<Stream, CancellationToken, Task> consume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(consume);
        var key = TemplateSourceKeys.File(templateId, version, file.Path);

        StorageResult<VerifiedRead> open;
        try
        {
            open = await _store.OpenReadAsync(key, file.Size, file.Sha256, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException)
        {
            _metrics.Object(TemplateSourceMetrics.FileKind, TemplateSourceMetrics.ErrorStatus);
            throw new SourceReadException(SourceReadFailureKind.Unreachable, UnavailableReason);
        }

        if (!open.Succeeded || open.Value is null)
        {
            _metrics.Object(TemplateSourceMetrics.FileKind, TemplateSourceMetrics.ErrorStatus);
            throw Failure(open.Outcome, file.Path);
        }

        try
        {
            await using var verified = open.Value;
            await consume(verified.Content, cancellationToken).ConfigureAwait(false);
            _metrics.Object(TemplateSourceMetrics.FileKind, TemplateSourceMetrics.OkStatus);
        }
        catch (StorageIntegrityException)
        {
            _metrics.Object(TemplateSourceMetrics.FileKind, TemplateSourceMetrics.ErrorStatus);
            throw new SourceReadException(
                SourceReadFailureKind.Invalid,
                $"'{Printable(file.Path)}' does not match the size and hash the publication record declares.");
        }
    }

    private async Task<SourceVersionRead> ReadCoreAsync(string templateId, string version, CancellationToken cancellationToken)
    {
        var descriptors = new List<SourceDescriptorName>();
        try
        {
            var prefix = TemplateSourceKeys.PublishedVersionPrefix(templateId, version);
            using (TemplateSourceMetrics.ActivitySource.StartActivity("template.source.list"))
            {
                await foreach (var info in _store.ListAsync(prefix, cancellationToken).ConfigureAwait(false))
                {
                    if (TemplateSourceKeys.TryReadDescriptorName(info.Key, templateId, version, out var sha256, out var reason))
                    {
                        descriptors.Add(new SourceDescriptorName(info.Key, sha256, info.Size));
                    }
                    else
                    {
                        Skip(info.Key, reason);
                    }
                }
            }
        }
        catch (StorageException)
        {
            return new SourceVersionRead.Unreachable(UnavailableReason);
        }

        if (descriptors.Count == 0)
        {
            return await AbsentOrIncompleteAsync(templateId, version, cancellationToken).ConfigureAwait(false);
        }

        if (descriptors.Count > 1)
        {
            return new SourceVersionRead.Duplicate(descriptors);
        }

        var descriptor = descriptors[0];
        StorageResult<VerifiedRead> open;
        try
        {
            using var activity = TemplateSourceMetrics.ActivitySource.StartActivity("template.source.open");
            open = await _store.OpenReadAsync(descriptor.Key, descriptor.Size, descriptor.Sha256, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StorageException)
        {
            _metrics.Object(TemplateSourceMetrics.DescriptorKind, TemplateSourceMetrics.ErrorStatus);
            return new SourceVersionRead.Unreachable(UnavailableReason);
        }

        if (!open.Succeeded || open.Value is null)
        {
            _metrics.Object(TemplateSourceMetrics.DescriptorKind, TemplateSourceMetrics.ErrorStatus);
            return open.Outcome switch
            {
                StorageOutcome.NotFound => new SourceVersionRead.Unreachable(ListingDisagreesReason),
                StorageOutcome.IntegrityFailure => new SourceVersionRead.Refused(new SourceReadRefusal(
                    SourceReadRefusalKind.Invalid,
                    "The publication record does not match the hash its name declares.")),
                _ => new SourceVersionRead.Unreachable(UnavailableReason),
            };
        }

        PublicationDescriptorRead parsed;
        try
        {
            await using var verified = open.Value;
            parsed = PublicationDescriptorReader.Read(verified.Content);
        }
        catch (StorageIntegrityException)
        {
            _metrics.Object(TemplateSourceMetrics.DescriptorKind, TemplateSourceMetrics.ErrorStatus);
            return new SourceVersionRead.Refused(new SourceReadRefusal(
                SourceReadRefusalKind.Invalid,
                "The publication record does not match the hash its name declares."));
        }

        if (!parsed.Succeeded || parsed.Descriptor is null)
        {
            _metrics.Object(TemplateSourceMetrics.DescriptorKind, TemplateSourceMetrics.ErrorStatus);
            return new SourceVersionRead.Refused(new SourceReadRefusal(
                SourceReadRefusalKind.Invalid,
                parsed.Reason ?? "The publication record is not one this service reads."));
        }

        var refusal = PublicationChecks.Check(parsed.Descriptor, templateId, version, _options);
        if (refusal is not null)
        {
            _metrics.Object(TemplateSourceMetrics.DescriptorKind, TemplateSourceMetrics.ErrorStatus);
            return new SourceVersionRead.Refused(refusal);
        }

        _metrics.Object(TemplateSourceMetrics.DescriptorKind, TemplateSourceMetrics.OkStatus);
        return new SourceVersionRead.Found(new SourcePublication(
            templateId,
            version,
            descriptor.Key,
            descriptor.Sha256,
            descriptor.Size,
            parsed.Descriptor.Files,
            parsed.Descriptor.FileCount,
            parsed.Descriptor.TotalSize));
    }

    private async Task<SourceVersionRead> AbsentOrIncompleteAsync(string templateId, string version, CancellationToken cancellationToken)
    {
        var prefix = TemplateSourceKeys.FilesPrefix(templateId, version);
        var count = 0;
        try
        {
            using (TemplateSourceMetrics.ActivitySource.StartActivity("template.source.list"))
            {
                await foreach (var info in _store.ListAsync(prefix, cancellationToken).ConfigureAwait(false))
                {
                    if (TemplateSourceKeys.IsUnderPrefix(info.Key, prefix))
                    {
                        count++;
                    }
                }
            }
        }
        catch (StorageException)
        {
            return new SourceVersionRead.Unreachable(UnavailableReason);
        }

        return count > 0 ? new SourceVersionRead.Incomplete(count) : new SourceVersionRead.Absent();
    }

    private async Task<string?> ReadVersionTextAsync(SourceDescriptorName descriptor, CancellationToken cancellationToken)
    {
        StorageResult<VerifiedRead> open;
        try
        {
            using var activity = TemplateSourceMetrics.ActivitySource.StartActivity("template.source.open");
            open = await _store.OpenReadAsync(descriptor.Key, descriptor.Size, descriptor.Sha256, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StorageException)
        {
            _metrics.Object(TemplateSourceMetrics.DescriptorKind, TemplateSourceMetrics.ErrorStatus);
            throw new TemplateSourceException(TemplateSourceFailure.Unreachable, UnavailableReason);
        }

        if (!open.Succeeded || open.Value is null)
        {
            _metrics.Object(TemplateSourceMetrics.DescriptorKind, TemplateSourceMetrics.ErrorStatus);
            if (open.Outcome is StorageOutcome.Transient or StorageOutcome.Permanent)
            {
                throw new TemplateSourceException(TemplateSourceFailure.Unreachable, UnavailableReason);
            }

            return null;
        }

        try
        {
            await using var verified = open.Value;
            var parsed = PublicationDescriptorReader.Read(verified.Content);
            _metrics.Object(
                TemplateSourceMetrics.DescriptorKind,
                parsed.Succeeded ? TemplateSourceMetrics.OkStatus : TemplateSourceMetrics.ErrorStatus);
            return parsed.Succeeded ? parsed.Descriptor?.Version : null;
        }
        catch (StorageIntegrityException)
        {
            _metrics.Object(TemplateSourceMetrics.DescriptorKind, TemplateSourceMetrics.ErrorStatus);
            return null;
        }
    }

    private SourceVersionRead Finish(SourceVersionRead read, string templateId, string version, long started)
    {
        var state = read switch
        {
            SourceVersionRead.Found => TemplateSourceMetrics.States.Found,
            SourceVersionRead.Absent => TemplateSourceMetrics.States.Absent,
            SourceVersionRead.Incomplete => TemplateSourceMetrics.States.Incomplete,
            SourceVersionRead.Duplicate => TemplateSourceMetrics.States.Duplicate,
            SourceVersionRead.Unreachable => TemplateSourceMetrics.States.Unreachable,
            SourceVersionRead.Refused => TemplateSourceMetrics.States.Refused,
            _ => TemplateSourceMetrics.States.Refused,
        };

        var elapsed = Stopwatch.GetElapsedTime(started);
        _metrics.Read(state, elapsed);

        var descriptor = (read as SourceVersionRead.Found)?.Publication;
        var fileCount = descriptor?.FileCount
            ?? (read as SourceVersionRead.Incomplete)?.FileCount
            ?? 0;
        var bytes = descriptor?.TotalSize ?? 0;

        _logger.Log(
            state == TemplateSourceMetrics.States.Unreachable ? LogLevel.Error : LogLevel.Information,
            new EventId(EventIds.Read, "template.source.read"),
            "template.source.read template {TemplateId} version {Version} state {State}, publication {DescriptorSha256}, files {FileCount}, bytes {TotalSize}, duration {DurationMs} ms",
            Printable(templateId),
            Printable(version),
            state,
            descriptor?.DescriptorSha256 ?? "(none)",
            fileCount,
            bytes,
            (long)elapsed.TotalMilliseconds);

        return read;
    }

    private void Skip(ObjectKey key, string reason)
    {
        var segment = key.Segments.Count > 0 ? key.Segments[^1] : "(none)";
        _logger.LogInformation(
            new EventId(EventIds.SkippedEntry, "template.source.skipped_entry"),
            "template.source.skipped_entry segment {Segment}: {Reason}",
            Printable(segment),
            reason);
    }

    private static SourceReadException Failure(StorageOutcome outcome, string path) =>
        outcome switch
        {
            StorageOutcome.NotFound => new SourceReadException(
                SourceReadFailureKind.Invalid,
                $"The publication record names '{Printable(path)}', which the source does not hold."),
            StorageOutcome.IntegrityFailure => new SourceReadException(
                SourceReadFailureKind.Invalid,
                $"'{Printable(path)}' does not match the size and hash the publication record declares."),
            _ => new SourceReadException(SourceReadFailureKind.Unreachable, UnavailableReason),
        };

    private static string Printable(string? value) => PublicationChecks.Printable(value);

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9._-]{0,126}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateIdentifier();

    /// <summary>The identifiers of this category's log records.</summary>
    private static class EventIds
    {
        /// <summary>One record per exact read, on every path.</summary>
        public const int Read = 4201;

        /// <summary>One record per entry outside the layout.</summary>
        public const int SkippedEntry = 4202;
    }
}
