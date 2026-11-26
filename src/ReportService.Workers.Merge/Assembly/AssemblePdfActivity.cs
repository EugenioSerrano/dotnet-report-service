using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReportService.Jobs;
using ReportService.Pdf.Assembly;
using ReportService.Pdf.Verification;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace ReportService.Workers.Merge.Assembly;

/// <summary>
/// Assembles one output from its committed parts: it reads the partition and the
/// part commits ordered by block index, refuses a partition with a missing commit,
/// copies every part through a verifying read against its recorded size and hash,
/// re-checks the document ceiling, resolves the job's pinned package and its
/// numbering contract, assembles and structurally verifies the document, and only
/// then streams the verified file to the object store under the attempt's own
/// output key. It beats while it works, honours cancellation, removes everything it
/// wrote on temporary disk, and observes the temporary-disk quota of three times
/// the parts' bytes. A test-only seam can make the process exit after a chosen
/// batch; no normal composition installs it.
/// </summary>
public sealed class AssemblePdfActivity(
    IJobStore jobs,
    ITemplateVersionStore versions,
    IObjectStore objects,
    TakenTemplatePackageReader packages,
    IPdfAssembler assembler,
    AssemblyJobMetrics metrics,
    ILoggerFactory loggers,
    ILogger<AssemblePdfActivity> logger,
    string? scratchRoot = null)
{
    /// <summary>The name the step is registered and scheduled under.</summary>
    public const string Name = "AssemblePdf";

    /// <summary>The code a document that failed its structural verification ends with.</summary>
    public const string ArtifactIntegrityFailed = "ARTIFACT_INTEGRITY_FAILED";

    /// <summary>The code an assembly over the document ceiling ends with.</summary>
    public const string OutputLimitExceeded = "OUTPUT_LIMIT_EXCEEDED";

    /// <summary>The code a package that cannot be stamped ends with.</summary>
    public const string TemplateInvalid = "TEMPLATE_INVALID";

    /// <summary>The code a state the step cannot use ends with.</summary>
    public const string ExecutionFailed = "EXECUTION_FAILED";

    /// <summary>The code the temporary-disk quota ends with.</summary>
    public const string QuotaExceeded = "QUOTA_EXCEEDED";

    /// <summary>The directory the taken copies and the staging files live under, when the caller names none.</summary>
    public static readonly string DefaultScratchRoot =
        Path.Combine(Path.GetTempPath(), "report-service-assembly");

    /// <summary>The directory the taken copies and the staging files live under.</summary>
    public string ScratchRoot { get; } = scratchRoot ?? DefaultScratchRoot;

    /// <summary>
    /// The deterministic interruption the composition installs for the restart test.
    /// A normal process holds none, and no other assembly can install one.
    /// </summary>
    internal AssemblyInterruption? Interruption { get; set; }

    /// <summary>Assembles one output.</summary>
    /// <param name="input">The job and the output.</param>
    [Activity(Name)]
    public async Task<AssemblePdfResult> RunAsync(AssemblePdfInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        using var context = JobContext.Begin(input.ReportJobId, loggers);
        System.Diagnostics.Activity.Current?.SetTag("output.id", input.OutputId);

        var partition = await jobs.FindPartitionAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false)
            ?? throw Refuse(ExecutionFailed, "the job has no partition");
        if (partition.BlockCount < 1)
        {
            throw Refuse(ExecutionFailed, "the partition holds no block");
        }

        var commits = await jobs.ListPartsAsync(input.OutputId, cancellationToken).ConfigureAwait(false);
        var ordered = Ordered(commits, partition.BlockCount);
        var reference = await jobs.FindTemplateReferenceAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false)
            ?? throw Refuse(ExecutionFailed, "the job is not recorded");
        var pin = reference.Pin ?? throw Refuse(ExecutionFailed, "the job's template version was never resolved");
        var record = await versions.FindAsync(pin.TemplateId, pin.ResolvedVersion, cancellationToken).ConfigureAwait(false)
            ?? throw Refuse(ExecutionFailed, "the job's template version was never taken");
        if (!string.Equals(record.PackageSha256, pin.PackageSha256, StringComparison.Ordinal))
        {
            throw Refuse(ExecutionFailed, "the taken version does not match the job's pin");
        }

        var settings = PageSettings(record.ManifestJson)
            ?? throw Refuse(TemplateInvalid, "the package declares no usable page setting");
        var frame = PageFrame.FromMillimetres(
            (double)settings.WidthMm,
            (double)settings.HeightMm,
            (double)settings.MarginTopMm,
            (double)settings.MarginRightMm,
            (double)settings.MarginBottomMm,
            (double)settings.MarginLeftMm);

        var totalPages = ordered.Sum(part => (long)part.PageCount);
        var totalBytes = ordered.Sum(part => part.SizeBytes);
        if (totalPages > AssemblyLimits.MaximumPages)
        {
            throw Refuse(
                OutputLimitExceeded,
                "limit=document-pages limitValue=" + AssemblyLimits.MaximumPages.ToString(CultureInfo.InvariantCulture)
                + " reached=" + totalPages.ToString(CultureInfo.InvariantCulture));
        }

        if (totalBytes > AssemblyLimits.MaximumPartBytes)
        {
            throw Refuse(
                OutputLimitExceeded,
                "limit=document-bytes limitValue=" + AssemblyLimits.MaximumPartBytes.ToString(CultureInfo.InvariantCulture)
                + " reached=" + totalBytes.ToString(CultureInfo.InvariantCulture));
        }

        Directory.CreateDirectory(ScratchRoot);
        await using var package = await packages.OpenAsync(record, ScratchRoot, cancellationToken).ConfigureAwait(false);
        var contract = NumberingContract(record.Manifest, package.Root)
            ?? throw Refuse(TemplateInvalid, "the package declares no usable numbering contract");

        using var work = new AssemblyTemporaryDirectory(ScratchRoot, totalBytes);
        using var beatEnds = new CancellationTokenSource();
        var beat = BeatAsync(beatEnds.Token);
        try
        {
            var parts = await CopyPartsAsync(ordered, work, cancellationToken).ConfigureAwait(false);
            var output = Path.Combine(work.Root, "assembled.pdf");
            var request = new AssemblyRequest(
                parts,
                contract,
                output,
                work.Root,
                AssemblyLimits.BatchSize,
                Metrics: null,
                OnBatch: batch => BatchWrittenAsync(work, batch));

            AssemblyResult assembled;
            try
            {
                assembled = await assembler.AssembleAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (AssemblyLimitExceededException limit)
            {
                throw Refuse(
                    OutputLimitExceeded,
                    "limit=" + limit.Limit
                    + " limitValue=" + limit.LimitValue.ToString(CultureInfo.InvariantCulture)
                    + " reached=" + limit.ReachedValue.ToString(CultureInfo.InvariantCulture));
            }
            catch (AssemblyQuotaExceededException quota)
            {
                logger.LogWarning(
                    "Output {OutputId} not assembled: the temporary folder passed its quota of {LimitBytes} bytes",
                    input.OutputId,
                    quota.LimitBytes);
                throw Refuse(QuotaExceeded, "the assembly's temporary folder passed its quota");
            }
            catch (AssemblyInputException refusal)
            {
                throw Refuse(ArtifactIntegrityFailed, refusal.Reason);
            }

            AssemblyVerdict verdict = AssemblyVerdict.Failed(AssemblyFailureReasons.Unreadable);
            await assembled.Metrics.MeasureAsync(AssemblyStage.Verification, () =>
            {
                verdict = AssemblyVerifier.Verify(new AssemblyVerificationRequest(
                    assembled.OutputPath,
                    Expectations(ordered, parts),
                    frame,
                    contract));
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            if (!verdict.Passed)
            {
                logger.LogWarning(
                    "Output {OutputId} failed its structural verification: {Reason} on part {PartIndex} page {PageNumber}",
                    input.OutputId,
                    verdict.Reason,
                    verdict.PartIndex,
                    verdict.PageNumber);
                throw Refuse(
                    ArtifactIntegrityFailed,
                    "check=" + verdict.Reason
                    + " part=" + verdict.PartIndex?.ToString(CultureInfo.InvariantCulture)
                    + " page=" + verdict.PageNumber?.ToString(CultureInfo.InvariantCulture));
            }

            var key = AttemptKey(input);
            var localSize = new FileInfo(assembled.OutputPath).Length;
            var localSha = await Sha256Async(assembled.OutputPath, cancellationToken).ConfigureAwait(false);
            ObjectWrite written;
            await using (var content = new FileStream(assembled.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                var write = await objects.WriteAsync(key, content, cancellationToken).ConfigureAwait(false);
                if (write.Succeeded)
                {
                    written = write.Value!;
                }
                else if (write.Outcome == StorageOutcome.AlreadyExists)
                {
                    // A retried attempt writes its own attempt-scoped key again; the object it
                    // finds is the one this same attempt verified, so the local values are its.
                    written = new ObjectWrite(key, localSize, localSha, null);
                }
                else
                {
                    throw StorageFailure(write.Outcome, write.Error);
                }
            }

            if (written.Size != localSize || !string.Equals(written.Sha256, localSha, StringComparison.Ordinal))
            {
                throw Refuse(ArtifactIntegrityFailed, "the stored object does not match the verified document");
            }

            assembled.Metrics.RecordOutput(written.Size);
            metrics.Record(assembled.Metrics, verdict);
            logger.LogInformation(
                "Output {OutputId} assembled: {Pages} pages from {Parts} parts in {Batches} batches, {Bytes} bytes",
                input.OutputId,
                assembled.PageCount,
                assembled.Metrics.Parts,
                assembled.Metrics.Batches,
                written.Size);
            return new AssemblePdfResult(key.ToString(), written.Sha256, written.Size, assembled.PageCount);
        }
        finally
        {
            await beatEnds.CancelAsync().ConfigureAwait(false);
            await beat.ConfigureAwait(false);
        }
    }

    /// <summary>Orders the commits by block index and refuses a partition with a gap, a duplicate or a foreign block.</summary>
    /// <param name="commits">The output's part commits, in any order.</param>
    /// <param name="blockCount">How many blocks the partition holds.</param>
    private static IReadOnlyList<PartCommit> Ordered(IReadOnlyList<PartCommit> commits, int blockCount)
    {
        var byIndex = new Dictionary<int, PartCommit>(commits.Count);
        foreach (var commit in commits)
        {
            if (commit.BlockIndex < 0 || commit.BlockIndex >= blockCount)
            {
                throw Refuse(ExecutionFailed, "a part commit names a block outside the partition");
            }

            if (!byIndex.TryAdd(commit.BlockIndex, commit))
            {
                throw Refuse(ExecutionFailed, "a block has more than one part commit");
            }
        }

        var ordered = new List<PartCommit>(blockCount);
        for (var index = 0; index < blockCount; index++)
        {
            if (!byIndex.TryGetValue(index, out var commit))
            {
                throw Refuse(ExecutionFailed, "a block of the partition has no part commit");
            }

            ordered.Add(commit);
        }

        return ordered;
    }

    private async Task<IReadOnlyList<AssemblyPart>> CopyPartsAsync(
        IReadOnlyList<PartCommit> commits,
        AssemblyTemporaryDirectory work,
        CancellationToken cancellationToken)
    {
        var parts = new List<AssemblyPart>(commits.Count);
        foreach (var commit in commits)
        {
            var path = Path.Combine(
                work.Root,
                "part-" + commit.BlockIndex.ToString("0000", CultureInfo.InvariantCulture) + ".pdf");
            var read = await objects
                .OpenReadAsync(ObjectKey.Create(commit.ObjectKey), commit.SizeBytes, commit.Sha256, cancellationToken)
                .ConfigureAwait(false);
            if (!read.Succeeded || read.Value is null)
            {
                if (read.Outcome == StorageOutcome.Transient)
                {
                    throw StorageFailure(read.Outcome, read.Error);
                }

                throw Refuse(ArtifactIntegrityFailed, "a part object does not match its commit");
            }

            await using (var verified = read.Value)
            {
                try
                {
                    await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                    await verified.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
                catch (StorageIntegrityException)
                {
                    throw Refuse(ArtifactIntegrityFailed, "a part object does not match its commit");
                }
            }

            parts.Add(new AssemblyPart(commit.BlockIndex, path, commit.PageCount, new FileInfo(path).Length));
            work.EnsureWithinQuota();
        }

        return parts;
    }

    private async ValueTask BatchWrittenAsync(AssemblyTemporaryDirectory work, int batch)
    {
        ActivityExecutionContext.Current.Heartbeat();
        work.EnsureWithinQuota();
        Interruption?.AfterBatch(batch);
        await ValueTask.CompletedTask;
    }

    private static IReadOnlyList<AssemblyPartExpectation> Expectations(
        IReadOnlyList<PartCommit> commits,
        IReadOnlyList<AssemblyPart> parts) =>
        [.. commits.Select((commit, index) => new AssemblyPartExpectation(
            commit.BlockIndex,
            parts[index].Path,
            commit.PageCount,
            commit.SizeBytes,
            commit.Sha256))];

    private ObjectKey AttemptKey(AssemblePdfInput input)
    {
        var attempt = ActivityExecutionContext.Current.Info;
        var attemptId = AssemblyStepOptions.AttemptId(
            attempt.WorkflowId ?? string.Empty,
            attempt.WorkflowRunId ?? string.Empty,
            attempt.ActivityId,
            attempt.Attempt);
        return ObjectKey.FromSegments("outputs", input.ReportJobId, input.OutputId, attemptId);
    }

    private static PdfPageSettings? PageSettings(string manifestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("outputs", out var outputs)
                || outputs.ValueKind != JsonValueKind.Object
                || !outputs.TryGetProperty("pdf", out var pdf))
            {
                return null;
            }

            return PdfPageSettingsRules.Resolve(pdf).Settings;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private PageNumbering? NumberingContract(TemplateManifest manifest, string packageRoot)
    {
        var numbering = manifest.Outputs?.Pdf?.Numbering;
        if (numbering?.Area is not { } area
            || area.LeftMm is not { } left
            || area.TopMm is not { } top
            || area.WidthMm is not { } width
            || area.HeightMm is not { } height
            || width <= 0m
            || height <= 0m)
        {
            return null;
        }

        if (numbering.Font is not { } font
            || string.IsNullOrWhiteSpace(font.Family)
            || font.File is not { } file
            || !PackagePath.IsValid(file)
            || font.SizePt is not { } size
            || size <= 0m
            || size > 72m
            || !PageNumbering.IsWellFormedFormat(numbering.Format)
            || string.IsNullOrWhiteSpace(numbering.Culture))
        {
            return null;
        }

        try
        {
            _ = CultureInfo.GetCultureInfo(numbering.Culture);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(packageRoot, file.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(packageRoot), StringComparison.Ordinal) || !File.Exists(path))
        {
            return null;
        }

        return new PageNumbering(
            new NumberingArea((double)left, (double)top, (double)width, (double)height),
            new NumberingFont(font.Family, file, (double)size, File.ReadAllBytes(path)),
            numbering.Format!,
            numbering.Culture!);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private Task BeatAsync(CancellationToken cancellationToken) => Task.Run(
        async () =>
        {
            using var timer = new PeriodicTimer(AssemblyStepOptions.HeartbeatEvery);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    ActivityExecutionContext.Current.Heartbeat();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        });

    private static ApplicationFailureException Refuse(string code, string reason) =>
        new(reason, errorType: code, nonRetryable: true);

    private static StorageException StorageFailure(StorageOutcome outcome, StorageError? error) =>
        new(outcome, error ?? new StorageError("storage.unavailable", "the store is unavailable"));
}
