using System.Globalization;
using System.Text;
using ReportService.Testing;
using ReportService.Testing.MultiBlock;
using ReportService.Workers.Merge.Assembly;

namespace ReportService.EndToEndTests;

/// <summary>
/// The live path's configured document capacity over the whole local composition: the
/// composition is given a page capacity of its own, which every process that judges a
/// document's size receives; a valid grouped snapshot whose assembled document would cross
/// it is uploaded and confirmed against the running API, the live data step partitions it,
/// and the real Chromium worker renders the blocks until the part commit that would pass
/// the capacity. The output ends non-retryably with the limit code; its stored reason names
/// the limit, the configured value - not the example profile's - and the value the report
/// reached; the reached value is above the configured value and above the committed parts'
/// own sum; and nothing is published - no artifact row, no output-class object, no partial PDF.
/// </summary>
public sealed class OverCapacityPdfJobEndToEndTests : IDisposable
{
    // The fixture prints a few hundred pages before the crossing commit; the covering run
    // records its own --timeout and this deadline is the test's own bound.
    private static readonly TimeSpan OverCapacityDeadline = TimeSpan.FromMinutes(30);

    private readonly MultiBlockStage _stage = new(
        accounted: true,
        templateId: "over-capacity",
        snapshotFactory: _ => MultiBlockFixtures.CapacitySnapshot(MultiBlockFixtures.OverCapacityGroups),
        packageFactory: MultiBlockFixtures.CapacityPackage,
        compositionArguments:
        [
            "--DocumentCapacity:MaximumPages=" + MultiBlockFixtures.OverCapacityPageLimit.ToString(CultureInfo.InvariantCulture),
            "--DocumentCapacity:ProfileName=e2e-over-capacity",
        ]);

    // The same capacity, with one render and one merge worker per priority class.
    private readonly MultiBlockStage _priorityStage = new(
        accounted: true,
        templateId: "over-capacity",
        snapshotFactory: _ => MultiBlockFixtures.CapacitySnapshot(MultiBlockFixtures.OverCapacityGroups),
        packageFactory: MultiBlockFixtures.CapacityPackage,
        compositionArguments:
        [
            "--DocumentCapacity:MaximumPages=" + MultiBlockFixtures.OverCapacityPageLimit.ToString(CultureInfo.InvariantCulture),
            "--DocumentCapacity:ProfileName=e2e-over-capacity",
            "--Composition:PriorityClassWorkers=true",
        ]);

    /// <inheritdoc />
    public void Dispose()
    {
        _stage.Dispose();
        _priorityStage.Dispose();
    }

    /// <summary>
    /// Priority never changes the document-size capacity: the same document beyond the configured
    /// pages, submitted at low and at high, fails with the same limit error and publishes nothing.
    /// </summary>
    [Fact]
    public async Task TheSameDocumentBeyondTheCapacityFailsTheSameWayAtLowAndAtHigh()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _priorityStage.StartAsync(cancellationToken);
        await _priorityStage.TakeTemplateAsync(composition, cancellationToken);

        var jobs = new List<string>();
        foreach (var priority in new[] { "low", "high" })
        {
            var uploadId = await _priorityStage.SeedConfirmingUploadAsync(composition, cancellationToken, MultiBlockFixtures.OverCapacityRecords);
            jobs.Add(await _priorityStage.SubmitAsync(composition, uploadId, cancellationToken, priority: priority));
            await _priorityStage.CompleteUploadAsync(composition, uploadId, cancellationToken);
        }

        var ended = new List<MultiBlockStage.EndedJob>();
        foreach (var job in jobs)
        {
            ended.Add(await _priorityStage.WaitForEndAsync(composition, job, cancellationToken, OverCapacityDeadline));
        }

        Assert.All(ended, job =>
        {
            Assert.Equal("FAILED", job.Status);
            Assert.Equal(AssemblePdfActivity.OutputLimitExceeded, job.OutputErrorCode);
            Assert.Null(job.ArtifactId);
        });
        Assert.Equal(ended[0].OutputErrorCode, ended[1].OutputErrorCode);
    }

    [Fact]
    public async Task ADocumentBeyondTheConfiguredPagesFailsWithTheLimitErrorNamingTheConfiguredValueAndPublishesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _stage.StartAsync(cancellationToken);
        await _stage.TakeTemplateAsync(composition, cancellationToken);
        var uploadId = await _stage.SeedConfirmingUploadAsync(
            composition,
            cancellationToken,
            MultiBlockFixtures.OverCapacityRecords);
        var reportJobId = await _stage.SubmitAsync(composition, uploadId, cancellationToken);
        await _stage.CompleteUploadAsync(composition, uploadId, cancellationToken);

        var ended = await _stage.WaitForEndAsync(composition, reportJobId, cancellationToken, OverCapacityDeadline);

        // The code is the delivered one, read from the assembly that re-checks the capacity.
        Assert.Equal(AssemblePdfActivity.OutputLimitExceeded, ended.OutputErrorCode);
        Assert.Equal("FAILED", ended.Status);
        Assert.NotNull(ended.OutputId);
        Assert.Equal("FAILED", ended.OutputStatus);
        Assert.NotNull(ended.OutputErrorReason);
        Assert.Null(ended.ArtifactId);
        Assert.Null(ended.ObjectKey);
        Assert.Null(ended.SizeBytes);

        // The stored reason names the limit, the limit's value and the value reached.
        var fields = ended.OutputErrorReason!
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        Assert.True(fields.TryGetValue("limit", out var limit), "the reason names the limit");
        Assert.True(fields.TryGetValue("limitValue", out var limitValueText), "the reason names the limit's value");
        Assert.True(fields.TryGetValue("reached", out var reachedText), "the reason names the reached value");
        var limitValue = long.Parse(limitValueText!, CultureInfo.InvariantCulture);
        var reached = long.Parse(reachedText!, CultureInfo.InvariantCulture);

        // The page dimension is the one the fixture crosses, and the value named is the
        // composition's configured capacity, not the example profile's.
        Assert.Equal("document-pages", limit);
        Assert.Equal(MultiBlockFixtures.OverCapacityPageLimit, limitValue);
        Assert.True(reached > limitValue, $"the reached value {reached} is above the limit {limitValue}");

        // The crossing commit was rolled back: the committed parts stay within the limit,
        // and the reached value is above their own sum.
        var parts = await _stage.ReadPartsAsync(composition, ended.OutputId!, cancellationToken);
        var committedPages = parts.Sum(part => part.PageCount);
        Assert.True(parts.Count > 0, "the blocks before the crossing one were committed");
        Assert.True(committedPages <= limitValue, $"the committed parts hold {committedPages} pages");
        Assert.True(reached > committedPages, $"the reached value {reached} is above the committed {committedPages}");

        // Nothing is published: no output-class object and no artifact row.
        var outputs = new List<string>();
        await foreach (var info in composition.Storage.ListAsync($"outputs/{reportJobId}", cancellationToken))
        {
            outputs.Add(info.Key.ToString());
        }

        Assert.Empty(outputs);

        // The committed sums and the reached value stay with the run's evidence.
        RunEnvironment.WriteEvidence(
            "over-capacity.evidence.txt",
            Encoding.UTF8.GetBytes(
                $"code={ended.OutputErrorCode} reason={ended.OutputErrorReason} "
                + $"committedBlocks={parts.Count} committedPages={committedPages.ToString(CultureInfo.InvariantCulture)} "
                + $"reached={reached.ToString(CultureInfo.InvariantCulture)} "
                + $"outputObjects={outputs.Count}{Environment.NewLine}"));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"over-capacity: blocks={parts.Count} committedPages={committedPages} reached={reached}");
    }
}
