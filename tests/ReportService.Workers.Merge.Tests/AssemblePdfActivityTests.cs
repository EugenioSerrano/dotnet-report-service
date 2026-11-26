using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Pdf.Assembly;
using ReportService.Storage;
using ReportService.TemplatePackages.Packages;
using ReportService.Workers.Merge.Assembly;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Merge.Tests;

/// <summary>
/// The assembly step: it reads the ordered committed parts, refuses a partition with
/// a gap, re-checks the ceiling, requires a numbering contract, assembles and
/// verifies in manifest order, writes the one output-class object under the
/// attempt's key, removes its temporary files, and stops after a chosen batch when
/// the test-only interruption is installed. A failure carries the code the output
/// ends with and never a data value.
/// </summary>
public sealed class AssemblePdfActivityTests
{
    [Fact]
    public async Task ItAssemblesTheCommittedPartsAndWritesOneOutputObject()
    {
        using var harness = new MergeStepHarness(blocks: 2, pagesEach: 2);

        var result = await new ActivityEnvironment().RunAsync(() =>
            harness.Activity().RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId)));

        Assert.Equal(4, result.PageCount);
        Assert.StartsWith(
            $"outputs/{MergeStepHarness.JobId}/{MergeStepHarness.OutputId}/",
            result.ObjectKey,
            StringComparison.Ordinal);
        Assert.True(result.SizeBytes > 0);
        Assert.Equal(64, result.Sha256.Length);

        var stored = await harness.Store.OpenReadAsync(
            ObjectKey.Create(result.ObjectKey), result.SizeBytes, result.Sha256, TestContext.Current.CancellationToken);
        Assert.True(stored.Succeeded, stored.Outcome.ToString());
        stored.Value!.Dispose();
    }

    [Fact]
    public async Task ItRemovesItsTemporaryFilesAfterSuccess()
    {
        using var harness = new MergeStepHarness(blocks: 2, pagesEach: 1);
        var scratch = Path.Combine(harness.Root, "scratch");

        await new ActivityEnvironment().RunAsync(() =>
            harness.Activity().RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId)));

        Assert.False(Directory.Exists(scratch) && Directory.EnumerateDirectories(scratch, "assembly-*").Any());
    }

    [Fact]
    public async Task APartitionWithAMissingCommitIsRefusedWithoutAssembling()
    {
        using var harness = new MergeStepHarness(blocks: 3, pagesEach: 1);
        harness.Jobs.RemovePart(MergeStepHarness.OutputId, 1);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            new ActivityEnvironment().RunAsync(() =>
                harness.Activity().RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId))));

        Assert.Equal(AssemblePdfActivity.ExecutionFailed, failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task APackageWithNoNumberingContractIsRefused()
    {
        using var harness = new MergeStepHarness(blocks: 2, pagesEach: 1, withNumbering: false);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            new ActivityEnvironment().RunAsync(() =>
                harness.Activity().RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId))));

        Assert.Equal(AssemblePdfActivity.TemplateInvalid, failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task TheCeilingIsRecheckedBeforeAnythingIsAssembled()
    {
        using var harness = new MergeStepHarness(blocks: 1, pagesEach: 1);
        var commit = harness.Commits[0];
        harness.Jobs.RemovePart(MergeStepHarness.OutputId, 0);
        harness.Jobs.SeedPart(commit with { PageCount = 10_001 });

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            new ActivityEnvironment().RunAsync(() =>
                harness.Activity().RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId))));

        Assert.Equal(AssemblePdfActivity.OutputLimitExceeded, failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("limit=document-pages", failure.Message, StringComparison.Ordinal);
        Assert.Contains("limitValue=10000", failure.Message, StringComparison.Ordinal);
        Assert.Contains("reached=10001", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATamperedPartFailsTheStructuralVerification()
    {
        using var harness = new MergeStepHarness(blocks: 2, pagesEach: 1);
        var commit = harness.Commits[0];
        var path = Path.Combine(harness.Root, "tampered.pdf");
        await File.WriteAllBytesAsync(path, "not the committed document"u8.ToArray(), TestContext.Current.CancellationToken);
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var key = ObjectKey.FromSegments("parts", MergeStepHarness.JobId, "0", "tampered");
        var write = await harness.Store.WriteAsync(key, new MemoryStream(bytes), TestContext.Current.CancellationToken);
        Assert.True(write.Succeeded);
        harness.Jobs.RemovePart(MergeStepHarness.OutputId, 0);
        await harness.Jobs.CommitPartAsync(
            commit with { ObjectKey = key.ToString(), Sha256 = write.Value!.Sha256, SizeBytes = write.Value.Size },
            TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            new ActivityEnvironment().RunAsync(() =>
                harness.Activity().RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId))));

        Assert.Equal(AssemblePdfActivity.ArtifactIntegrityFailed, failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task TheInterruptionSeamStopsTheProcessAfterTheChosenBatch()
    {
        using var harness = new MergeStepHarness(blocks: 2, pagesEach: 1);
        var exit = new SimulatedExit();

        var thrown = await Assert.ThrowsAsync<SimulatedExit>(() =>
            new ActivityEnvironment().RunAsync(() =>
                harness.Activity(new AssemblyInterruption(1, _ => throw exit))
                    .RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId))));

        Assert.Same(exit, thrown);
    }

    [Fact]
    public async Task TheOutputKeyIsAttemptScoped()
    {
        using var harness = new MergeStepHarness(blocks: 1, pagesEach: 1);

        var result = await new ActivityEnvironment().RunAsync(() =>
            harness.Activity().RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId)));

        var segments = result.ObjectKey.Split('/');
        Assert.Equal(4, segments.Length);
        Assert.Equal("outputs", segments[0]);
        Assert.Equal(MergeStepHarness.JobId, segments[1]);
        Assert.Equal(MergeStepHarness.OutputId, segments[2]);
        Assert.Equal(24, segments[3].Length);
        Assert.All(segments[3], character => Assert.True(char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'));
    }

    [Fact]
    public async Task APackageWithNoUsablePageSettingIsRefused()
    {
        using var harness = new MergeStepHarness(
            blocks: 1,
            pagesEach: 1,
            manifestJson: "{\"manifestVersion\":\"1.0\",\"id\":\"merge-template\",\"version\":\"1.0.0\"}");

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            new ActivityEnvironment().RunAsync(() =>
                harness.Activity().RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId))));

        Assert.Equal(AssemblePdfActivity.TemplateInvalid, failure.ErrorType);
    }

    [Fact]
    public async Task APackageWithoutTheFontItDeclaresIsRefused()
    {
        using var harness = new MergeStepHarness(blocks: 1, pagesEach: 1, includeFont: false);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            new ActivityEnvironment().RunAsync(() =>
                harness.Activity().RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId))));

        Assert.Equal(AssemblePdfActivity.TemplateInvalid, failure.ErrorType);
    }

    [Theory]
    [InlineData("format")]
    [InlineData("culture")]
    [InlineData("file")]
    [InlineData("area")]
    public async Task ANumberingContractWithAnUnusableMemberIsRefused(string member)
    {
        using var harness = new MergeStepHarness(
            blocks: 1,
            pagesEach: 1,
            numbering: contract => member switch
            {
                "format" => contract with { Format = "Page {X}" },
                "culture" => contract with { Culture = "not a culture" },
                "file" => contract with { Font = contract.Font! with { File = "../outside.ttf" } },
                _ => contract with { Area = contract.Area! with { WidthMm = 0 } },
            });

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            new ActivityEnvironment().RunAsync(() =>
                harness.Activity().RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId))));

        Assert.Equal(AssemblePdfActivity.TemplateInvalid, failure.ErrorType);
    }

    [Fact]
    public async Task APartWhoseBytesDoNotMatchItsCommitIsRefused()
    {
        using var harness = new MergeStepHarness(blocks: 1, pagesEach: 1);
        var commit = harness.Commits[0];
        harness.Jobs.RemovePart(MergeStepHarness.OutputId, 0);
        harness.Jobs.SeedPart(commit with { Sha256 = new string('0', 64) });

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            new ActivityEnvironment().RunAsync(() =>
                harness.Activity().RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId))));

        Assert.Equal(AssemblePdfActivity.ArtifactIntegrityFailed, failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task ATransientStoreFailureIsLeftToTheEnginesRetry()
    {
        using var harness = new MergeStepHarness(blocks: 1, pagesEach: 1);
        var activity = new AssemblePdfActivity(
            harness.Jobs,
            new FakeVersionStore(harness.Record),
            new TransientOnPartStore(harness.Store),
            new TakenTemplatePackageReader(harness.Store),
            new PdfSharpAssembler(),
            new AssemblyJobMetrics(ReportService.ServiceDefaults.MeterIdentity.ForHost("test")),
            NullLoggerFactory.Instance,
            NullLogger<AssemblePdfActivity>.Instance,
            Path.Combine(harness.Root, "scratch"));

        var failure = await Assert.ThrowsAsync<StorageException>(() =>
            new ActivityEnvironment().RunAsync(() =>
                activity.RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId))));

        Assert.Equal(StorageOutcome.Transient, failure.Outcome);
    }

    [Fact]
    public async Task ACommitOutsideThePartitionIsRefused()
    {
        using var harness = new MergeStepHarness(blocks: 1, pagesEach: 1);
        var commit = harness.Commits[0];
        harness.Jobs.SeedPart(commit with { BlockIndex = 7 });

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            new ActivityEnvironment().RunAsync(() =>
                harness.Activity().RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId))));

        Assert.Equal(AssemblePdfActivity.ExecutionFailed, failure.ErrorType);
    }

    [Fact]
    public void TheInterruptionVariableIsReadOnlyWhenItNamesAPositiveBatch()
    {
        var original = Environment.GetEnvironmentVariable(AssemblyInterruption.AfterBatchVariable);
        try
        {
            Environment.SetEnvironmentVariable(AssemblyInterruption.AfterBatchVariable, null);
            Assert.Null(AssemblyInterruption.FromEnvironment());
            Environment.SetEnvironmentVariable(AssemblyInterruption.AfterBatchVariable, "0");
            Assert.Null(AssemblyInterruption.FromEnvironment());
            Environment.SetEnvironmentVariable(AssemblyInterruption.AfterBatchVariable, "2");
            Assert.NotNull(AssemblyInterruption.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AssemblyInterruption.AfterBatchVariable, original);
        }
    }

    [Fact]
    public void TheStepIsNamedAsTheOrchestrationSchedulesIt() =>
        Assert.Equal("AssemblePdf", AssemblePdfActivity.Name);

    private sealed class SimulatedExit : Exception;

    private sealed class TransientOnPartStore(IObjectStore inner) : IObjectStore
    {
        public StorageCapabilities Capabilities => inner.Capabilities;

        public Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken) =>
            inner.WriteAsync(key, content, cancellationToken);

        public Task<StorageResult<VerifiedRead>> OpenReadAsync(
            ObjectKey key,
            long expectedSize,
            string expectedSha256,
            CancellationToken cancellationToken) =>
            key.ToString().StartsWith("parts/", StringComparison.Ordinal)
                ? Task.FromResult(StorageResult.Failure<VerifiedRead>(
                    StorageOutcome.Transient,
                    new StorageError("storage.unavailable", "the store is unavailable")))
                : inner.OpenReadAsync(key, expectedSize, expectedSha256, cancellationToken);

        public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetInfoAsync(key, cancellationToken);

        public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, cancellationToken);

        public IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, CancellationToken cancellationToken) =>
            inner.ListAsync(classPrefix, cancellationToken);
    }
}
