using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplateSource.Source;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using ReportService.Workers.Orchestration.Uploads;
using ReportService.Workers.Pdf.Rendering;

namespace ReportService.Workers.Pdf.Tests.Rendering;

/// <summary>
/// The part a render publishes under a long deployment root, and the path budget
/// of every class of object the product stores. One case writes the render's own
/// key under a root of exactly 111 characters, against a real Filesystem provider
/// in a temporary directory, and reads it back verified; the other pins every
/// number the operator-facing page states, so the page and the code cannot drift.
/// </summary>
public sealed class ThePartKeyFitsALongStorageRootTests : IDisposable
{
    // 111 characters: the root the defect was met under, rebuilt here as the boundary.
    private const int RootLength = 111;

    // The sibling a write creates beside its object: one dot, ".tmp-" and 32 hex.
    private const int TemporarySiblingLength = 38;

    // The operating system's own path bound, which the provider's 248 stays below.
    private const int OperatingSystemPathBound = 260;

    private readonly string head = Path.Combine(
        Path.GetTempPath(), "report-service-tests", "b063-" + Guid.NewGuid().ToString("N"));

    private readonly string root;

    public ThePartKeyFitsALongStorageRootTests()
    {
        Assert.True(
            head.Length + 1 < RootLength,
            $"this host's temporary path leaves no room for a {RootLength}-character root: the case's own directory would be {head.Length} characters under {Path.GetTempPath()}");

        root = Path.Combine(head, new string('r', RootLength - head.Length - 1));
        Assert.Equal(RootLength, root.Length);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheRenderPublishesItsPartUnderARootOf111Characters()
    {
        var store = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = root, CreateIfMissing = true },
            Path.GetTempPath(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);

        var jobId = JobIdentifier.Create();
        var outputId = JobIdentifier.CreateOutput();
        var attemptId = RenderPdfBlockOptions.AttemptId("job", "run", "activity", 1);
        var key = ObjectKey.FromSegments("outputs", jobId, outputId, attemptId);
        var resolved = Path.Combine([root, .. key.Segments]);
        var body = Encoding.UTF8.GetBytes("a printed part, published under a long deployment root");

        StorageResult<ObjectWrite> write;
        await using (var content = new MemoryStream(body, writable: false))
        {
            write = await store.WriteAsync(key, content, Cancellation);
        }

        // Every number the defect is argued with travels in the failure itself, so the red of this
        // case records 149 / 111 / 261 without a second, hand-counted assertion to rewrite.
        Assert.True(
            write.Outcome == StorageOutcome.Success,
            $"the store answered {write.Outcome} ({write.Error?.Code}); key {key.ToString().Length}, root {root.Length}, resolved {resolved.Length}");

        Assert.True(File.Exists(resolved), "the object is not at the path its key resolves to");

        var read = await store.OpenReadAsync(key, write.Value!.Size, write.Value.Sha256, Cancellation);
        Assert.True(read.Succeeded, $"the verified read answered {read.Outcome} ({read.Error?.Code})");

        await using (var verified = read.Value!)
        {
            using var buffer = new MemoryStream();
            await verified.Content.CopyToAsync(buffer, Cancellation);
            Assert.Equal(body, buffer.ToArray());
        }

        var deleted = await store.DeleteAsync(key, Cancellation);
        Assert.True(deleted.Succeeded, $"the delete answered {deleted.Outcome} ({deleted.Error?.Code})");
    }

    [Fact]
    public void ThePathBudgetOfEveryKeyClassIsTheOneTheOperatorReads()
    {
        // The published part: "outputs" 7 + 1 + the job id 36 + 1 + the output id 39 + 1 + the attempt 24.
        var jobId = JobIdentifier.Create();
        var outputId = JobIdentifier.CreateOutput();
        var partAttempt = RenderPdfBlockOptions.AttemptId("job", "run", "activity", 1);
        var partKey = ObjectKey.FromSegments("outputs", jobId, outputId, partAttempt);
        var partSegments = partKey.Segments;

        Assert.Equal(["outputs", jobId, outputId, partAttempt], partSegments);
        Assert.Equal([7, 36, 39, 24], partSegments.Select(segment => segment.Length));
        Assert.Equal(JobIdentifier.RandomHexLength, jobId.Length - JobIdentifier.JobPrefix.Length);
        Assert.Equal(JobIdentifier.RandomHexLength, outputId.Length - JobIdentifier.OutputPrefix.Length);

        var part = partKey.ToString().Length;
        Assert.Equal(109, part);
        Assert.Equal(221, Path.Combine([root, .. partSegments]).Length);
        Assert.Equal(259, Path.Combine([root, .. partSegments]).Length + TemporarySiblingLength);
        Assert.Equal(248, FilesystemObjectStore.MaxPathLength);

        // The other fixed-width compositions, each length read from its own identifiers.
        var uploadId = UploadIdentifier.Create();
        Assert.Equal(UploadIdentifier.RandomHexLength, uploadId.Length - UploadIdentifier.Prefix.Length);
        var staging = ObjectKey.FromSegments("staging", uploadId, "content").ToString().Length;
        Assert.Equal(55, staging);

        // The health probe is the staging class's longest key. Its 47 is restated from the composition
        // - "staging/probe" and the "/" before the 32 hex - because no type exposes the probe's key,
        // and the process names are literals at the three compositions; "orchestration-worker" is the
        // longest of them. A fourth process name longer than it is not caught by this case.
        var probeProcesses = new[] { "api", "orchestration-worker", "pdf-worker" };
        Assert.All(probeProcesses, name => Assert.True(47 + name.Length <= 67, $"the probe under {name} is {47 + name.Length} characters"));
        Assert.Equal(67, 47 + "orchestration-worker".Length);
        var probe = ObjectKey.FromSegments("staging", "probe", "orchestration-worker", new string('0', 32)).ToString().Length;
        Assert.Equal(67, probe);

        var snapshotAttempt = UploadVerificationActivities.AttemptId("job", "run", "activity", 1);
        Assert.Equal(64, snapshotAttempt.Length);
        var snapshots = ObjectKey.FromSegments("snapshots", uploadId, snapshotAttempt).ToString().Length;
        Assert.Equal(114, snapshots);

        var pipelineAttempt = PdfOutputActivityOptions.AttemptId("job", "run", "activity", 1);
        Assert.Equal(64, pipelineAttempt.Length);
        var block = ObjectKey.FromSegments("block", jobId, "0", pipelineAttempt).ToString().Length;
        var index = ObjectKey.FromSegments("index", jobId, "0", pipelineAttempt).ToString().Length;
        Assert.Equal(109, block);
        Assert.Equal(109, index);

        // A value one character past the raw bound is hashed, so an encoded segment is 34 characters
        // whatever its own text is.
        var encoded = new string('a', 33);
        Assert.Equal(KeySegmentEncoding.HashedLength, KeySegmentEncoding.Encode(encoded).Length);
        Assert.Equal(34, KeySegmentEncoding.HashedLength);

        Assert.Equal(179, TemplatePackageKeys.MaxKeyLength);
        var templates = TemplatePackageKeys.ForVersion(encoded, encoded, encoded, new string('f', 64)).ToString().Length;
        Assert.Equal(179, templates);

        var record = TemplateSourceKeys.Descriptor(encoded, encoded, new string('e', 64)).ToString().Length;
        Assert.Equal(165, record);

        // A version's file key reaches 371: eight path segments that are not raw-eligible each encode
        // to 34, so the path's 15 characters of text carry 279 characters of key.
        const string eightSegments = "-/-/-/-/-/-/-/-";
        Assert.Equal(15, eightSegments.Length);
        Assert.True(PackagePath.IsValid(eightSegments, out var refusal), refusal);
        Assert.Equal(8, PackagePath.MaxSegments);
        Assert.Equal(34, KeySegmentEncoding.Encode("-").Length);

        var fileKey = TemplateSourceKeys.File(encoded, encoded, eightSegments);
        var file = fileKey.ToString().Length;
        Assert.Equal(371, file);
        Assert.Equal(12, fileKey.Segments.Count);
        Assert.True(fileKey.Segments.Count < ObjectKey.MaxSegments);
        Assert.True(file < ObjectKey.MaxLength);

        // The operator's page states, for every class, the resolved length under a root of 111.
        Assert.Equal(167, RootLength + 1 + staging);
        Assert.Equal(179, RootLength + 1 + probe);
        Assert.Equal(226, RootLength + 1 + snapshots);
        Assert.Equal(221, RootLength + 1 + block);
        Assert.Equal(221, RootLength + 1 + index);
        Assert.Equal(291, RootLength + 1 + templates);
        Assert.Equal(277, RootLength + 1 + record);
        Assert.Equal(483, RootLength + 1 + file);

        // The ceilings: the longest root a read takes against the provider's 248, and the longest a
        // write takes against the 222 of a runtime that does not extend paths (260 less the sibling).
        var readBound = FilesystemObjectStore.MaxPathLength;
        var writeBound = OperatingSystemPathBound - TemporarySiblingLength;
        Assert.Equal(222, writeBound);

        Assert.Equal(138, readBound - 1 - part);
        Assert.Equal(112, writeBound - 1 - part);
        Assert.Equal(192, readBound - 1 - staging);
        Assert.Equal(166, writeBound - 1 - staging);
        Assert.Equal(180, readBound - 1 - probe);
        Assert.Equal(154, writeBound - 1 - probe);
        Assert.Equal(133, readBound - 1 - snapshots);
        Assert.Equal(107, writeBound - 1 - snapshots);
        Assert.Equal(68, readBound - 1 - templates);
        Assert.Equal(42, writeBound - 1 - templates);
        Assert.Equal(82, readBound - 1 - record);
        Assert.True(file > readBound, "no root takes the template-source file key");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!Directory.Exists(head))
        {
            return;
        }

        RemoveLinks(new DirectoryInfo(head));
        try
        {
            Directory.Delete(head, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // A junction is not removed by a recursive delete on Windows; links are unlinked first, never followed.
    private static void RemoveLinks(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if (entry.LinkTarget is not null)
            {
                entry.Delete();
            }
            else if (entry is DirectoryInfo child)
            {
                RemoveLinks(child);
            }
        }
    }
}
