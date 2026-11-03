using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.Storage;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.TemplatePackages.Versioning;
using ReportService.TemplateSource.Checks;
using ReportService.TemplateSource.Manifest;

namespace ReportService.TemplateSource.Take;

/// <summary>
/// The take: it reads one version of one template from the configured source at
/// one commit, keeps it as one write-once object under the content-addressed key
/// and makes it usable by an insert-once record — once. A version already taken
/// is answered from its record without touching the source; a version this
/// consumer may not use is answered exactly like one that does not exist, before
/// any refusal that could name a file of the package; a version that the source
/// changed under a take that had already read it is logged and never replaces the
/// copy. Nothing is written before the version's owner is known, and the scratch
/// directory is removed on every path.
/// <para>
/// The requesting consumer is a parameter, not an ambient value: the take decides
/// visibility twice, on the record it meets and on the manifest of the version it
/// read, and the checks of the seam run only for a caller that may use the
/// version.
/// </para>
/// </summary>
public sealed partial class TemplateTake
{
    /// <summary>The log category of the take.</summary>
    public const string LogCategory = "ReportService.TemplateSource";

    /// <summary>How long one take may run: the source's own deadline, the manifest read and the checks.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(240);

    /// <summary>The directory under the temporary directory every take's scratch area is made in.</summary>
    public const string ScratchRootName = "report-service/takes";

    private const string NotFoundReason = "The template version was not found.";

    private const string SourceUnavailableReason = "The template source could not be read. The version may be retried later.";

    private const string SourceMisconfiguredReason = "The template source is not configured for the repository and branch this service reads. The version may be retried later.";

    private const string StorageReason = "The taken copy could not be stored. The version may be retried later.";

    private const string StoreReason = "The template version store did not answer. The take may be retried later.";

    private const string TakeDeadlineReason = "The take did not finish in the time it is given. The version may be retried later.";

    private static readonly TimeSpan DeadlineSlack = TimeSpan.FromSeconds(1);

    private readonly ITemplateSource _source;

    private readonly ITemplateVersionStore _versions;

    private readonly IObjectStore _objects;

    private readonly TemplateManifestReader _manifests;

    private readonly TemplateVersionConflict _conflict;

    private readonly IEnumerable<ITemplateVersionCheck> _checks;

    private readonly TemplateTakeMetrics _metrics;

    private readonly TimeProvider _clock;

    private readonly TemplateSourceOptions _options;

    private readonly ILogger<TemplateTake> _logger;

    /// <summary>Creates the take with the collaborators the process owns.</summary>
    /// <param name="source">The configured template source.</param>
    /// <param name="versions">The taken versions.</param>
    /// <param name="objects">The object store the packages are kept in.</param>
    /// <param name="manifests">The one reader of <c>template.yaml</c>.</param>
    /// <param name="conflict">The warning a changed taken version produces.</param>
    /// <param name="checks">The checks a candidate passes before it is stored.</param>
    /// <param name="metrics">The take's instruments.</param>
    /// <param name="clock">The clock the take's timestamps are read from.</param>
    /// <param name="options">The bounds one read stays inside.</param>
    /// <param name="logger">The take's log.</param>
    public TemplateTake(
        ITemplateSource source,
        ITemplateVersionStore versions,
        IObjectStore objects,
        TemplateManifestReader manifests,
        TemplateVersionConflict conflict,
        IEnumerable<ITemplateVersionCheck> checks,
        TemplateTakeMetrics metrics,
        TimeProvider clock,
        IOptions<TemplateSourceOptions> options,
        ILogger<TemplateTake> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(conflict);
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _versions = versions;
        _objects = objects;
        _manifests = manifests;
        _conflict = conflict;
        _checks = checks;
        _metrics = metrics;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Takes one version on behalf of one consumer.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version to take.</param>
    /// <param name="requestingConsumerId">The consumer the version is taken for; a version it may not use is answered as missing.</param>
    /// <param name="commit">The commit a caller already resolved, or <see langword="null"/> to read the configured branch.</param>
    /// <param name="reportJobId">The job the take runs for, when there is one.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public async Task<TemplateTakeOutcome> TakeAsync(
        string templateId,
        string version,
        string requestingConsumerId,
        SourceCommit? commit,
        string? reportJobId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(requestingConsumerId);

        var started = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline + DeadlineSlack);

        TemplateTakeOutcome outcome;
        try
        {
            outcome = await TakeCoreAsync(templateId, version, requestingConsumerId, commit, reportJobId, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            outcome = TemplateTakeOutcome.Refused(TemplateTakeCodes.ExecutionFailed, TakeDeadlineReason, retryable: true);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        _metrics.TakeEnded(outcome.Name, (outcome as RefusedVersion)?.Code ?? TemplateTakeMetrics.NoCode, elapsed);
        _logger.Log(
            outcome is RefusedVersion refusal ? LogLevel.Warning : LogLevel.Information,
            new EventId(EventIds.Take, "template.take"),
            "{Level} template {TemplateId} version {Version} for consumer {RequestingConsumerId} ended {Outcome} {Code} in {DurationMs} ms, job {ReportJobId}",
            outcome is RefusedVersion ? "refused" : "accepted",
            templateId,
            version,
            requestingConsumerId,
            outcome.Name,
            (outcome as RefusedVersion)?.Code ?? TemplateTakeMetrics.NoCode,
            (long)elapsed.TotalMilliseconds,
            reportJobId ?? "(none)");
        return outcome;
    }

    private async Task<TemplateTakeOutcome> TakeCoreAsync(
        string templateId,
        string version,
        string requestingConsumerId,
        SourceCommit? commit,
        string? reportJobId,
        CancellationToken cancellationToken)
    {
        // 1. A name that cannot be a version of a template is answered as missing,
        //    without a database call and without a source read.
        if (!TemplateIdentifier().IsMatch(templateId) || !SemanticVersionSyntax.IsValid(version))
        {
            return TemplateTakeOutcome.Refused(TemplateTakeCodes.TemplateNotFound, NotFoundReason, retryable: false);
        }

        // 2. A version already taken is answered from its record, for a caller that
        //    may use it; the source is not read.
        var existing = await _versions.FindAsync(templateId, version, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return AnswerFor(existing, requestingConsumerId);
        }

        string? scratch = null;
        try
        {
            var read = await ReadVersionAsync(templateId, version, commit, cancellationToken).ConfigureAwait(false);
            if (read.Refusal is not null)
            {
                return read.Refusal;
            }

            var taken = read.Value!;
            scratch = taken.Scratch;

            // 5. The manifest, read once, by the one reader of YAML.
            using var manifestFile = File.OpenRead(Path.Combine(scratch, PackagePath.ManifestFileName));
            var manifestRead = _manifests.Read(manifestFile);
            if (!manifestRead.Succeeded)
            {
                return TemplateTakeOutcome.Refused(TemplateTakeCodes.TemplateInvalid, manifestRead.Reason!, retryable: false);
            }

            // The reader accepts one document whose root is a mapping, so this is a mapping.
            var document = (JsonObject)TemplateManifestJson.Parse(manifestRead.CanonicalJson!);

            // 6. Whose version it is — before every content refusal that could name a file.
            var owner = await OwnerAsync(document, requestingConsumerId, cancellationToken).ConfigureAwait(false);
            if (owner.Refusal is not null)
            {
                return owner.Refusal;
            }

            var identity = owner.Value!;
            var content = await ContentAsync(templateId, version, document, manifestRead, taken, cancellationToken)
                .ConfigureAwait(false);
            if (content.Refusal is not null)
            {
                return content.Refusal;
            }

            var candidate = content.Value!;

            // 8. The checks of the seam run only for a caller that may use the version.
            var check = await TemplateVersionChecks.RunAsync(
                _checks,
                new TemplateCandidate(templateId, version, candidate.Manifest, scratch, taken.Commit.CommitSha),
                cancellationToken).ConfigureAwait(false);
            if (!check.Passed)
            {
                return TemplateTakeOutcome.Refused(check.Code!, check.Reason!, retryable: false);
            }

            // 9. A take that started before another one committed meets it here.
            var raced = await _versions.FindAsync(templateId, version, cancellationToken).ConfigureAwait(false);
            if (raced is not null)
            {
                return BindOrAnswer(raced, requestingConsumerId, candidate, taken.Commit.CommitSha, reportJobId);
            }

            // 10. The copy, at its content-addressed key, written once.
            var written = await StoreAsync(identity.Owner, templateId, version, candidate, cancellationToken).ConfigureAwait(false);
            if (written.Refusal is not null)
            {
                return written.Refusal;
            }

            var record = new TemplateVersionRecord(
                templateId,
                version,
                identity.Owner,
                identity.Shared,
                ManifestVersionText,
                candidate.Manifest.Engine!.Value,
                TemplateEngineCatalog.VersionOf(candidate.Manifest.Engine!.Value),
                candidate.SchemaRef,
                taken.Commit.CommitSha,
                taken.Folder.TreeSha,
                candidate.PackageSha256,
                candidate.PackageSize,
                candidate.FileCount,
                written.Value!,
                candidate.Manifest,
                manifestRead.CanonicalJson!,
                _clock.GetUtcNow());

            // 11. Visible only through the record, and only once.
            var insert = await _versions.TryInsertAsync(record, cancellationToken).ConfigureAwait(false);
            return insert.WasInserted
                ? TemplateTakeOutcome.Taken(insert.Record)
                : BindOrAnswer(insert.Record, requestingConsumerId, candidate, taken.Commit.CommitSha, reportJobId);
        }
        catch (TemplateSourceException exception)
        {
            return FromSource(exception);
        }
        catch (TemplateStoreUnavailableException exception)
        {
            _logger.LogError(new EventId(EventIds.StoreUnavailable, "template.store"), "The template version store did not answer: {ExceptionType}", exception.GetType().Name);
            return TemplateTakeOutcome.Refused(TemplateTakeCodes.ExecutionFailed, StoreReason, retryable: true);
        }
        finally
        {
            Delete(scratch);
        }
    }

    /// <summary>The one document of the version's manifest is <c>1.0</c>; the record's column holds it.</summary>
    private const string ManifestVersionText = "1.0";

    /// <summary>Reads the version's folder at one commit and copies its files to a scratch area.</summary>
    private async Task<Step<ReadVersion>> ReadVersionAsync(
        string templateId,
        string version,
        SourceCommit? commit,
        CancellationToken cancellationToken)
    {
        var pinned = commit
            ?? await RequestAsync(TemplateTakeMetrics.CommitRequest, () => _source.ResolveBranchAsync(cancellationToken)).ConfigureAwait(false);

        var folder = await RequestAsync(
            TemplateTakeMetrics.TreeRequest,
            () => _source.FindVersionFolderAsync(pinned, templateId, version, cancellationToken)).ConfigureAwait(false);
        if (folder is null)
        {
            return Step<ReadVersion>.Fail(TemplateTakeOutcome.Refused(TemplateTakeCodes.TemplateNotFound, NotFoundReason, retryable: false));
        }

        var files = await RequestAsync(
            TemplateTakeMetrics.TreeRequest,
            () => _source.ListFilesAsync(folder, cancellationToken)).ConfigureAwait(false);

        if (Structure(files) is { } refusal)
        {
            return Step<ReadVersion>.Fail(refusal);
        }

        var scratch = Path.Combine(Path.GetTempPath(), ScratchRootName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            foreach (var file in files.OrderBy(entry => entry.Path, StringComparer.Ordinal))
            {
                var target = Path.Combine(scratch, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await _source.OpenFileAsync(
                    file,
                    async (content, token) =>
                    {
                        await using var copy = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        await content.CopyToAsync(copy, token).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
                _metrics.SourceRequest(TemplateTakeMetrics.BlobRequest, TemplateTakeMetrics.OkStatus);
            }
        }
        catch (TemplateSourceException exception)
        {
            _metrics.SourceRequest(TemplateTakeMetrics.BlobRequest, TemplateTakeMetrics.ErrorStatus);
            Delete(scratch);
            return Step<ReadVersion>.Fail(FromSource(exception));
        }
        catch
        {
            Delete(scratch);
            throw;
        }

        return Step<ReadVersion>.Ok(new ReadVersion(pinned, folder, files, scratch));
    }

    /// <summary>The rules that make a copy safe to store, refused without naming a file.</summary>
    private TemplateTakeOutcome? Structure(IReadOnlyList<SourceFile> files)
    {
        if (files.Count > _options.MaxFiles)
        {
            return TemplateTakeOutcome.Refused(
                TemplateTakeCodes.TemplateInvalid,
                $"The package holds more files than this service takes (at most {_options.MaxFiles.ToString(CultureInfo.InvariantCulture)}).",
                retryable: false);
        }

        long total = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file.Size > _options.MaxFileBytes)
            {
                return TemplateTakeOutcome.Refused(
                    TemplateTakeCodes.TemplateInvalid,
                    $"A file is larger than this service takes (at most {_options.MaxFileBytes.ToString(CultureInfo.InvariantCulture)} bytes).",
                    retryable: false);
            }

            total += file.Size;
            if (total > _options.MaxPackageBytes)
            {
                return TemplateTakeOutcome.Refused(
                    TemplateTakeCodes.TemplateInvalid,
                    $"The package is larger than this service takes (at most {_options.MaxPackageBytes.ToString(CultureInfo.InvariantCulture)} bytes).",
                    retryable: false);
            }

            if (!PackagePath.IsValid(file.Path) || !paths.Add(file.Path))
            {
                return TemplateTakeOutcome.Refused(
                    TemplateTakeCodes.TemplateInvalid,
                    "A file's path is not one a template package may hold.",
                    retryable: false);
            }
        }

        // A path that is also a folder of another entry cannot be extracted as a file.
        foreach (var path in paths)
        {
            for (var index = path.IndexOf('/', StringComparison.Ordinal); index > 0; index = path.IndexOf('/', index + 1))
            {
                if (paths.Contains(path[..index]))
                {
                    return TemplateTakeOutcome.Refused(
                        TemplateTakeCodes.TemplateInvalid,
                        "The package holds a file and a folder under the same name.",
                        retryable: false);
                }
            }
        }

        if (!paths.Contains(PackagePath.ManifestFileName))
        {
            return TemplateTakeOutcome.Refused(
                TemplateTakeCodes.TemplateInvalid,
                "The package has no template.yaml at its root.",
                retryable: false);
        }

        return null;
    }

    /// <summary>The version's owner and sharing, and the gate that precedes every content refusal.</summary>
    private Task<Step<Identity>> OwnerAsync(JsonObject document, string requestingConsumerId, CancellationToken cancellationToken)
    {
        var ownerNode = document["owner"];
        if (ownerNode is null || ownerNode.GetValueKind() == JsonValueKind.Null)
        {
            return Task.FromResult(Step<Identity>.Fail(
                TemplateTakeOutcome.Refused(TemplateTakeCodes.TemplateInvalid, "The owner is missing in template.yaml.", retryable: false)));
        }

        if (ownerNode.GetValueKind() != JsonValueKind.String || ownerNode.GetValue<string>() is not { Length: > 0 } owner)
        {
            return Task.FromResult(Step<Identity>.Fail(
                TemplateTakeOutcome.Refused(TemplateTakeCodes.TemplateInvalid, "The owner in template.yaml must be a single value.", retryable: false)));
        }

        if (!ConsumerIdentifier().IsMatch(owner))
        {
            return Task.FromResult(Step<Identity>.Fail(TemplateTakeOutcome.Refused(
                TemplateTakeCodes.TemplateInvalid,
                "The owner in template.yaml is not a consumer identifier: letters, digits, '.', '_' and '-', 1 to 128 characters.",
                retryable: false)));
        }

        var sharedNode = document["shared"];
        var sharedKind = sharedNode?.GetValueKind() ?? JsonValueKind.False;
        if (sharedNode is not null && sharedKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Task.FromResult(Step<Identity>.Fail(TemplateTakeOutcome.Refused(
                TemplateTakeCodes.TemplateInvalid,
                "The 'shared' member of template.yaml must be true or false.",
                retryable: false)));
        }

        var shared = sharedKind == JsonValueKind.True;
        if (!shared && !string.Equals(owner, requestingConsumerId, StringComparison.Ordinal))
        {
            // Another consumer's version is answered exactly like one that does not exist:
            // nothing is named, and nothing of the package reaches the caller.
            return Task.FromResult(Step<Identity>.Fail(
                TemplateTakeOutcome.Refused(TemplateTakeCodes.TemplateNotFound, NotFoundReason, retryable: false)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Step<Identity>.Ok(new Identity(owner, shared)));
    }

    /// <summary>The rules that build a record, refused after the gate and naming files and fields.</summary>
    private async Task<Step<Candidate>> ContentAsync(
        string templateId,
        string version,
        JsonObject document,
        ManifestReadResult manifestRead,
        ReadVersion taken,
        CancellationToken cancellationToken)
    {
        var engineNode = document["engine"];
        if (engineNode is null || engineNode.GetValueKind() == JsonValueKind.Null)
        {
            return Step<Candidate>.Fail(
                TemplateTakeOutcome.Refused(TemplateTakeCodes.TemplateInvalid, "The engine is missing in template.yaml.", retryable: false));
        }

        if (engineNode.GetValueKind() != JsonValueKind.String || engineNode.GetValue<string>() is not { } engineName)
        {
            return Step<Candidate>.Fail(
                TemplateTakeOutcome.Refused(TemplateTakeCodes.TemplateInvalid, "The engine in template.yaml must be a single value.", retryable: false));
        }

        if (!TemplateEngineNames.TryParse(engineName, out var engine))
        {
            return Step<Candidate>.Fail(TemplateTakeOutcome.Refused(
                TemplateTakeCodes.UnsupportedCapability,
                $"The engine '{Printable(engineName)}' is not one this deployment renders.",
                retryable: false));
        }

        if (manifestRead.Manifest is not { } manifest)
        {
            return Step<Candidate>.Fail(TemplateTakeOutcome.Refused(
                TemplateTakeCodes.TemplateInvalid,
                "template.yaml declares a member with a type this service does not read.",
                retryable: false));
        }

        if (!string.Equals(manifest.ManifestVersion, ManifestVersionText, StringComparison.Ordinal))
        {
            return Step<Candidate>.Fail(TemplateTakeOutcome.Refused(
                TemplateTakeCodes.TemplateInvalid,
                $"template.yaml must declare manifestVersion '{ManifestVersionText}'.",
                retryable: false));
        }

        if (!string.Equals(manifest.Id, templateId, StringComparison.Ordinal))
        {
            return Step<Candidate>.Fail(TemplateTakeOutcome.Refused(
                TemplateTakeCodes.TemplateInvalid,
                $"The manifest id '{Printable(manifest.Id)}' does not match the template folder '{Printable(templateId)}'.",
                retryable: false));
        }

        if (!string.Equals(manifest.Version, version, StringComparison.Ordinal))
        {
            return Step<Candidate>.Fail(TemplateTakeOutcome.Refused(
                TemplateTakeCodes.TemplateInvalid,
                $"The folder templates/{Printable(templateId)}/{Printable(version)}/ does not match the version '{Printable(manifest.Version)}' declared in template.yaml. "
                + "Rename the folder or change the version so that they are equal.",
                retryable: false));
        }

        var schemaRef = manifest.DataContract?.Schema;
        if (schemaRef is null
            || !PackagePath.IsValid(schemaRef)
            || !File.Exists(Path.Combine(taken.Scratch, schemaRef.Replace('/', Path.DirectorySeparatorChar))))
        {
            return Step<Candidate>.Fail(TemplateTakeOutcome.Refused(
                TemplateTakeCodes.TemplateInvalid,
                "The data contract's schema is missing from the package, or it is not a file of the package.",
                retryable: false));
        }

        if (manifest.Outputs?.Pdf is { } pdf && !AnchorsWellFormed(pdf.Verification?.Anchors))
        {
            return Step<Candidate>.Fail(TemplateTakeOutcome.Refused(
                TemplateTakeCodes.TemplateInvalid,
                "outputs.pdf.verification.anchors is missing or not a list of field paths.",
                retryable: false));
        }

        var (packageSha256, packageSize) = await ArchiveAsync(taken, cancellationToken).ConfigureAwait(false);
        return Step<Candidate>.Ok(new Candidate(
            manifest,
            engine.Value,
            schemaRef,
            packageSha256,
            packageSize,
            taken.Files.Count,
            taken.Scratch));
    }

    /// <summary>Writes the package's archive to a scratch file and hashes the bytes it wrote.</summary>
    private static async Task<(string Sha256, long Size)> ArchiveAsync(ReadVersion taken, CancellationToken cancellationToken)
    {
        var sources = taken.Files
            .Select(file => PackageFileSource.FromFile(taken.Scratch, file.Path))
            .ToList();

        var archivePath = Path.Combine(taken.Scratch, "package.tar");
        await using var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var hashing = new HashingWriteStream(file, leaveOpen: true);
        await TemplatePackageArchive.WriteAsync(sources, hashing, cancellationToken).ConfigureAwait(false);
        await hashing.CompleteAsync(cancellationToken).ConfigureAwait(false);
        return (hashing.Sha256, hashing.Size);
    }

    /// <summary>Writes the copy once, and answers what the store holds at its key afterwards.</summary>
    private async Task<Step<ObjectKey>> StoreAsync(
        string owner,
        string templateId,
        string version,
        Candidate candidate,
        CancellationToken cancellationToken)
    {
        var key = TemplatePackageKeys.ForVersion(owner, templateId, version, candidate.PackageSha256);
        await using var archive = new FileStream(
            Path.Combine(candidate.Scratch, "package.tar"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var write = await _objects.WriteAsync(key, archive, cancellationToken).ConfigureAwait(false);
        switch (write.Outcome)
        {
            case StorageOutcome.Success when string.Equals(write.Value!.Sha256, candidate.PackageSha256, StringComparison.Ordinal)
                && write.Value.Size == candidate.PackageSize:
                return Step<ObjectKey>.Ok(key);

            case StorageOutcome.Success:
                _logger.LogError(
                    new EventId(EventIds.StorageMismatch, "template.storage"),
                    "The stored object does not match the bytes that were read: {ExceptionType}",
                    "StorageIntegrity");
                return Step<ObjectKey>.Fail(TemplateTakeOutcome.Refused(TemplateTakeCodes.ExecutionFailed, StorageReason, retryable: true));

            case StorageOutcome.AlreadyExists:
                // The same content-addressed bytes are already there; they must verify.
                var verified = await VerifyAsync(key, candidate, cancellationToken).ConfigureAwait(false);
                return verified
                    ? Step<ObjectKey>.Ok(key)
                    : Step<ObjectKey>.Fail(TemplateTakeOutcome.Refused(TemplateTakeCodes.ExecutionFailed, StorageReason, retryable: true));

            case StorageOutcome.Permanent:
                _logger.LogError(
                    new EventId(EventIds.StorageFailed, "template.storage"),
                    "The object store refused the write: {Code}",
                    write.Error?.Code ?? "storage.failed");
                return Step<ObjectKey>.Fail(TemplateTakeOutcome.Refused(TemplateTakeCodes.ExecutionFailed, StorageReason, retryable: true));

            default:
                return Step<ObjectKey>.Fail(TemplateTakeOutcome.Refused(TemplateTakeCodes.ExecutionFailed, StorageReason, retryable: true));
        }
    }

    private async Task<bool> VerifyAsync(ObjectKey key, Candidate candidate, CancellationToken cancellationToken)
    {
        var read = await _objects
            .OpenReadAsync(key, candidate.PackageSize, candidate.PackageSha256, cancellationToken)
            .ConfigureAwait(false);
        if (!read.Succeeded || read.Value is null)
        {
            return false;
        }

        try
        {
            await using var content = read.Value;
            await content.Content.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (StorageIntegrityException)
        {
            return false;
        }
    }

    /// <summary>Binds the record that exists when this caller may use it, and logs the difference.</summary>
    private TemplateTakeOutcome BindOrAnswer(
        TemplateVersionRecord record,
        string requestingConsumerId,
        Candidate candidate,
        string readCommitSha,
        string? reportJobId)
    {
        var same = string.Equals(record.PackageSha256, candidate.PackageSha256, StringComparison.Ordinal);
        if (!same)
        {
            _conflict.Report(record, candidate.PackageSha256, readCommitSha, reportJobId);
        }

        if (!TemplateVersionVisibility.MayUse(record, requestingConsumerId))
        {
            return TemplateTakeOutcome.Refused(TemplateTakeCodes.TemplateNotFound, NotFoundReason, retryable: false);
        }

        return same ? TemplateTakeOutcome.AlreadyTaken(record) : TemplateTakeOutcome.ConflictLogged(record);
    }

    private TemplateTakeOutcome AnswerFor(TemplateVersionRecord record, string requestingConsumerId) =>
        TemplateVersionVisibility.MayUse(record, requestingConsumerId)
            ? TemplateTakeOutcome.AlreadyTaken(record)
            : TemplateTakeOutcome.Refused(TemplateTakeCodes.TemplateNotFound, NotFoundReason, retryable: false);

    /// <summary>The answer the classification of the source's own observation gives.</summary>
    private TemplateTakeOutcome FromSource(TemplateSourceException exception)
    {
        switch (exception.Failure)
        {
            case TemplateSourceFailure.Unreachable:
                _logger.LogWarning(
                    new EventId(EventIds.SourceUnreachable, "template.source"),
                    "The template source could not be read: {ExceptionType}",
                    exception.GetType().Name);
                return TemplateTakeOutcome.Refused(TemplateTakeCodes.TemplateSourceUnavailable, SourceUnavailableReason, retryable: true);

            case TemplateSourceFailure.Misconfigured:
                _logger.LogError(
                    new EventId(EventIds.SourceMisconfigured, "source-misconfigured"),
                    "The template source does not serve the configured repository and branch: {ExceptionType}",
                    exception.GetType().Name);
                return TemplateTakeOutcome.Refused(TemplateTakeCodes.TemplateSourceUnavailable, SourceMisconfiguredReason, retryable: true);

            case TemplateSourceFailure.PackageTooLarge:
                // The client's own message names a folder; the caller gets the bound only.
                _logger.LogInformation(
                    new EventId(EventIds.SourceRefused, "template.source"),
                    "The package exceeds a bound the source reports: {ClientMessage}",
                    exception.Message);
                return TemplateTakeOutcome.Refused(
                    TemplateTakeCodes.TemplateInvalid,
                    "The template folder or the package holds more entries than the source will list in one answer.",
                    retryable: false);

            default:
                // A symbolic link or a submodule: the kind, never the path.
                _logger.LogInformation(
                    new EventId(EventIds.SourceRefused, "template.source"),
                    "The package holds an entry this service does not store: {ClientMessage}",
                    exception.Message);
                return TemplateTakeOutcome.Refused(
                    TemplateTakeCodes.TemplateInvalid,
                    "The package holds an entry this service does not store (a symbolic link or a submodule).",
                    retryable: false);
        }
    }

    /// <summary>Calls the source once, counting the request and its status class.</summary>
    private async Task<T> RequestAsync<T>(string kind, Func<Task<T>> request)
    {
        try
        {
            var value = await request().ConfigureAwait(false);
            _metrics.SourceRequest(kind, TemplateTakeMetrics.OkStatus);
            return value;
        }
        catch (TemplateSourceException)
        {
            _metrics.SourceRequest(kind, TemplateTakeMetrics.ErrorStatus);
            throw;
        }
    }

    /// <summary>A field path an author anchored, or a value a message may echo.</summary>
    private static bool AnchorsWellFormed(TemplatePdfAnchors? anchors) =>
        anchors?.Header is { } header
        && anchors.Item is { } item
        && header.All(path => AnchorPath().IsMatch(path))
        && item.All(path => AnchorPath().IsMatch(path));

    /// <summary>The text a message may echo: printable ASCII of at most 128 characters, or nothing.</summary>
    private static string Printable(string? value)
    {
        if (value is null || value.Length > 128 || value.Any(character => character < '\u0020' || character > '\u007E'))
        {
            return "(omitted)";
        }

        return value;
    }

    private static void Delete(string? directory)
    {
        if (directory is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover scratch directory is swept by the process's own cleanup.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9._-]{0,126}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateIdentifier();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex ConsumerIdentifier();

    [GeneratedRegex(@"^\$(\.[A-Za-z_][A-Za-z0-9_-]{0,127}){1,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex AnchorPath();

    /// <summary>The identifiers of this category's events.</summary>
    private static class EventIds
    {
        public const int Take = 4101;

        public const int SourceUnreachable = 4102;

        public const int SourceMisconfigured = 4103;

        public const int SourceRefused = 4104;

        public const int StorageMismatch = 4105;

        public const int StorageFailed = 4106;

        public const int StoreUnavailable = 4107;
    }

    /// <summary>One version as it was read: the commit, its folder, its file count and the scratch copy.</summary>
    private sealed record ReadVersion(SourceCommit Commit, SourceFolder Folder, IReadOnlyList<SourceFile> Files, string Scratch);

    /// <summary>The consumer the version belongs to, and whether every consumer may use it.</summary>
    private sealed record Identity(string Owner, bool Shared);

    /// <summary>A version that passed the rules that build a record, with its package hash and size.</summary>
    private sealed record Candidate(
        TemplateManifest Manifest,
        TemplateEngine Engine,
        string SchemaRef,
        string PackageSha256,
        long PackageSize,
        int FileCount,
        string Scratch);

    /// <summary>A step's value, or the answer that ended the take.</summary>
    private sealed record Step<T>(T? Value, TemplateTakeOutcome? Refusal)
    {
        public static Step<T> Ok(T value) => new(value, null);

        public static Step<T> Fail(TemplateTakeOutcome refusal) => new(default, refusal);
    }
}
