using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;
using ReportService.Workers.Pdf.Rendering;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Pdf.Tests.Rendering;

/// <summary>
/// The step's own binding between what was printed and what was stored: when the
/// store returns bytes other than the verified file's, the block is refused with
/// the delivered code, no part is committed and the object the write left behind
/// stays as an orphan; when the store returns the file's own size and hash, the
/// part is committed once under the key the file was written to.
/// </summary>
public sealed class WrongFileRefusalTests : IDisposable
{
    private readonly RenderStepHarness _harness = new();

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task AStoredBlockThatDoesNotMatchThePrintedFileIsRefusedAndLeavesAnOrphan()
    {
        var storage = await _harness.StorageAsync();
        var package = await RenderStepHarness.PackageAsync(storage.Store);
        var pin = RenderStepHarness.Pin(package.Sha256);
        var jobs = Jobs(pin, storage);
        var corrupting = new CorruptingObjectStore(storage.Store);
        var activity = _harness.Activity(jobs, new FakeVersionStore { Record = RecordOf(pin, package) }, new FakeRenderer(), corrupting);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(
                RenderStepHarness.JobId,
                RenderStepHarness.OutputId,
                0))));

        // The refusal is the delivered one, raised non-retryably by the step, and the
        // part is never committed: the output is left for the child to fail.
        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Null(jobs.Committed);

        // The write really happened: the object the comparison refused is under the
        // output's attempt key, referenced by no part commit - the orphan.
        var written = Directory.GetFiles(Path.Combine(
            _harness.Root, "store", "outputs", RenderStepHarness.JobId, RenderStepHarness.OutputId));
        Assert.Single(written);
    }

    [Fact]
    public async Task AStoreThatReturnsTheFilesOwnSizeAndHashCommitsThePartOnce()
    {
        var storage = await _harness.StorageAsync();
        var package = await RenderStepHarness.PackageAsync(storage.Store);
        var pin = RenderStepHarness.Pin(package.Sha256);
        var jobs = Jobs(pin, storage);
        var activity = _harness.Activity(jobs, new FakeVersionStore { Record = RecordOf(pin, package) }, new FakeRenderer(), storage.Store);

        var result = await new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(
            RenderStepHarness.JobId,
            RenderStepHarness.OutputId,
            0)));

        // The commit names the key the verified file was written to, and the answer
        // carries that commit's own values.
        Assert.NotNull(jobs.Committed);
        Assert.StartsWith(
            $"outputs/{RenderStepHarness.JobId}/{RenderStepHarness.OutputId}/",
            jobs.Committed!.ObjectKey,
            StringComparison.Ordinal);
        Assert.Equal(jobs.Committed.ObjectKey, result.PartKey);
        Assert.Equal(jobs.Committed.Sha256, result.Sha256);
        Assert.Equal(jobs.Committed.SizeBytes, result.SizeBytes);
    }

    private static FakeJobStore Jobs(TemplatePin pin, RenderStorage storage) => new()
    {
        Reference = RenderStepHarness.ReferenceOf(pin),
        Partition = new PartitionRecord(
            RenderStepHarness.JobId,
            1,
            1,
            1,
            storage.Block.SizeBytes,
            storage.Index.Key,
            storage.Index.Sha256,
            storage.Index.SizeBytes,
            DateTimeOffset.UnixEpoch),
    };

    private static TemplateVersionRecord RecordOf(TemplatePin pin, TakenPackage package) =>
        RenderStepHarness.Record(pin.PackageSha256!, package.Key, package.Size);
}

/// <summary>
/// The storage port with one difference: a write under the output namespace answers a
/// size and a hash that are not the file's own. The bytes are the real provider's,
/// written and left in place, so the refusal has an object to leave behind.
/// </summary>
internal sealed class CorruptingObjectStore(IObjectStore inner) : IObjectStore
{
    /// <inheritdoc />
    public StorageCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public async Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken)
    {
        var result = await inner.WriteAsync(key, content, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Value is not { } written)
        {
            return result;
        }

        return key.ToString().StartsWith("outputs/", StringComparison.Ordinal)
            ? StorageResult.Success(written with
            {
                Sha256 = new string('a', 64),
                Size = written.Size + 1,
            })
            : result;
    }

    /// <inheritdoc />
    public Task<StorageResult<VerifiedRead>> OpenReadAsync(ObjectKey key, long expectedSize, string expectedSha256, CancellationToken cancellationToken) =>
        inner.OpenReadAsync(key, expectedSize, expectedSha256, cancellationToken);

    /// <inheritdoc />
    public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.GetInfoAsync(key, cancellationToken);

    /// <inheritdoc />
    public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.DeleteAsync(key, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, CancellationToken cancellationToken) =>
        inner.ListAsync(classPrefix, cancellationToken);
}
