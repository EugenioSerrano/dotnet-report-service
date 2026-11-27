using System.Globalization;
using System.Text;
using ReportService.Testing;
using ReportService.Testing.MultiBlock;
using ReportService.Workers.Merge.Assembly;

namespace ReportService.EndToEndTests;

/// <summary>
/// The live path's document ceiling over the whole local composition: a valid grouped
/// snapshot whose assembled document would cross the published page limit is uploaded and
/// confirmed against the running API, the live data step partitions it, and the real
/// Chromium worker renders the blocks until the part commit that would pass the ceiling.
/// The output ends non-retryably with the delivered ceiling code; its stored reason names
/// the limit, the limit's value and the value the report reached; the reached value is
/// above the named limit and above the committed parts' own sum; and nothing is published -
/// no artifact row, no output-class object, no partial PDF.
/// </summary>
public sealed class OverCeilingPdfJobEndToEndTests : IDisposable
{
    // The fixture prints more than ten thousand pages before the crossing commit; the
    // covering run records its own --timeout and this deadline is the test's own bound.
    private static readonly TimeSpan OverCeilingDeadline = TimeSpan.FromMinutes(90);

    private readonly MultiBlockStage _stage = new(
        accounted: true,
        templateId: "over-ceiling",
        snapshotFactory: _ => MultiBlockFixtures.OverCeilingSnapshot(),
        packageFactory: MultiBlockFixtures.OverCeilingPackage);

    /// <inheritdoc />
    public void Dispose() => _stage.Dispose();

    [Fact]
    public async Task ADocumentBeyondThePageLimitFailsWithTheLimitErrorAndPublishesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _stage.StartAsync(cancellationToken);
        await _stage.TakeTemplateAsync(composition, cancellationToken);
        var uploadId = await _stage.SeedConfirmingUploadAsync(
            composition,
            cancellationToken,
            MultiBlockFixtures.OverCeilingRecords);
        var reportJobId = await _stage.SubmitAsync(composition, uploadId, cancellationToken);
        await _stage.CompleteUploadAsync(composition, uploadId, cancellationToken);

        var ended = await _stage.WaitForEndAsync(composition, reportJobId, cancellationToken, OverCeilingDeadline);

        // The code is the delivered one, read from the assembly that decides the ceiling.
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

        // The fixture is derived so the page limit is the one it crosses, and the published
        // value is the one the delivery applies.
        Assert.Equal("document-pages", limit);
        Assert.Equal(MultiBlockFixtures.OverCeilingPageLimit, limitValue);
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
            "over-ceiling.evidence.txt",
            Encoding.UTF8.GetBytes(
                $"code={ended.OutputErrorCode} reason={ended.OutputErrorReason} "
                + $"committedBlocks={parts.Count} committedPages={committedPages.ToString(CultureInfo.InvariantCulture)} "
                + $"reached={reached.ToString(CultureInfo.InvariantCulture)} "
                + $"outputObjects={outputs.Count}{Environment.NewLine}"));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"over-ceiling: blocks={parts.Count} committedPages={committedPages} reached={reached}");
    }
}
