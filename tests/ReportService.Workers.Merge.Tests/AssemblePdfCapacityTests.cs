using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Workers.Merge.Assembly;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Merge.Tests;

/// <summary>
/// The assembly step re-checks the deployment's configured capacity, not a compiled
/// value: a document exactly at the configured pages or bytes is assembled and published,
/// one page or one byte more ends non-retryably with the limit code naming the configured
/// value, and nothing is written to the output class. The temporary-disk quota adds one
/// maximum stored code per page exactly when the template declares a code.
/// </summary>
public sealed class AssemblePdfCapacityTests
{
    [Fact]
    public async Task ADocumentExactlyAtTheConfiguredPagesIsAssembled()
    {
        using var harness = new MergeStepHarness(blocks: 2, pagesEach: 2);
        var capacity = new DocumentCapacity(4, long.MaxValue, "exact-pages");

        var result = await new ActivityEnvironment().RunAsync(() =>
            harness.Activity(capacity: capacity).RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId)));

        Assert.Equal(4, result.PageCount);
    }

    [Fact]
    public async Task OnePageOverTheConfiguredPagesIsRefusedWithTheConfiguredValueAndNothingIsPublished()
    {
        using var harness = new MergeStepHarness(blocks: 2, pagesEach: 2);
        var capacity = new DocumentCapacity(3, long.MaxValue, "three-pages");

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            new ActivityEnvironment().RunAsync(() =>
                harness.Activity(capacity: capacity).RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId))));

        Assert.Equal(AssemblePdfActivity.OutputLimitExceeded, failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("limit=document-pages limitValue=3 reached=4", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("three-pages", failure.Message, StringComparison.Ordinal);
        await AssertNoOutputObjectAsync(harness);
    }

    [Fact]
    public async Task ADocumentExactlyAtTheConfiguredBytesIsAssembledAndOneByteLessIsRefused()
    {
        using var harness = new MergeStepHarness(blocks: 2, pagesEach: 2);
        var bytes = harness.Commits.Sum(commit => commit.SizeBytes);

        var assembled = await new ActivityEnvironment().RunAsync(() =>
            harness.Activity(capacity: new DocumentCapacity(int.MaxValue, bytes, "exact-bytes"))
                .RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId)));
        Assert.Equal(4, assembled.PageCount);

        using var refusedHarness = new MergeStepHarness(blocks: 2, pagesEach: 2);
        var refusedBytes = refusedHarness.Commits.Sum(commit => commit.SizeBytes);
        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            new ActivityEnvironment().RunAsync(() =>
                refusedHarness.Activity(capacity: new DocumentCapacity(int.MaxValue, refusedBytes - 1, "bytes-minus-one"))
                    .RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId))));

        Assert.Equal(AssemblePdfActivity.OutputLimitExceeded, failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("limit=document-bytes", failure.Message, StringComparison.Ordinal);
        Assert.Contains("limitValue=" + (refusedBytes - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStepRunsTheExampleProfileWhenTheDeploymentConfiguresNone()
    {
        using var harness = new MergeStepHarness(blocks: 1, pagesEach: 1);

        Assert.Equal(DocumentCapacity.Example, harness.Activity().Capacity);
    }

    [Theory]
    [InlineData(1_000L, 10L, false, 3_000L)]
    [InlineData(1_000L, 10L, true, 3_000L + (10L * 16 * 1024))]
    [InlineData(0L, 0L, true, 3L)]
    [InlineData(37_748_736L, 50_000L, true, (37_748_736L * 3) + (50_000L * 16 * 1024))]
    public void TheQuotaAddsOneMaximumStoredCodePerPageOnlyWhenACodeIsDeclared(long partBytes, long pages, bool codeDeclared, long expected) =>
        Assert.Equal(expected, AssemblyTemporaryDirectory.Quota(partBytes, pages, codeDeclared));

    [Fact]
    public void AFolderCreatedWithACodeCarriesTheAllowance()
    {
        var root = Path.Combine(Path.GetTempPath(), "capacity-quota-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var folder = new AssemblyTemporaryDirectory(root, 1_000, pages: 4, codeDeclared: true);

            Assert.Equal(3_000 + (4 * 16 * 1024), folder.QuotaBytes);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task AssertNoOutputObjectAsync(MergeStepHarness harness)
    {
        await foreach (var item in harness.Store.ListAsync("outputs", TestContext.Current.CancellationToken))
        {
            Assert.Fail("an output-class object was written: " + item.Key);
        }
    }
}
