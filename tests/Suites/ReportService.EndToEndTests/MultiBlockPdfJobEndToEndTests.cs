using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReportService.Data.Partitioning;
using ReportService.Pdf.Verification;
using ReportService.Testing;
using ReportService.Testing.Fixtures;

namespace ReportService.EndToEndTests;

/// <summary>
/// The live path's multi-block report over the whole local composition: a grouped snapshot
/// of a few thousand records - several blocks at the governed limits - is uploaded and
/// confirmed against the running API, the job is accepted with <c>pdf</c>, and the live data
/// step partitions it into the durable index; the real Chromium worker renders every block,
/// the real merge worker assembles them in manifest order and stamps one global page number,
/// and the published PDF is read back through the storage port and the product's reader:
/// one artifact, its page count the committed parts' sum, every record once in manifest
/// order, the numbering continuous, and the grand total the test's own sum. The job and its
/// output succeed, the orchestration worker's log carries neither a single-block refusal nor
/// a single-block start-up line, and the partition record's block count is the index's.
/// </summary>
public sealed class MultiBlockPdfJobEndToEndTests : IDisposable
{
    private static readonly TimeSpan LiveJobDeadline = TimeSpan.FromMinutes(30);

    private readonly MultiBlockStage _stage = new(accounted: true);

    /// <inheritdoc />
    public void Dispose() => _stage.Dispose();

    [Fact]
    public async Task TheLiveDataStepPublishesOneCorrectMultiBlockPdf()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _stage.StartAsync(cancellationToken);
        await _stage.TakeTemplateAsync(composition, cancellationToken);
        var uploadId = await _stage.SeedConfirmingUploadAsync(composition, cancellationToken);
        var reportJobId = await _stage.SubmitAsync(composition, uploadId, cancellationToken);
        await _stage.CompleteUploadAsync(composition, uploadId, cancellationToken);

        var ended = await _stage.WaitForEndAsync(composition, reportJobId, cancellationToken, LiveJobDeadline);

        Assert.Equal("SUCCEEDED", ended.Status);
        Assert.Equal("SUCCEEDED", ended.OutputStatus);
        Assert.Null(ended.ErrorCode);
        Assert.Null(ended.OutputErrorCode);
        Assert.NotNull(ended.ArtifactId);
        Assert.NotNull(ended.OutputId);
        Assert.NotNull(ended.ObjectKey);
        Assert.NotNull(ended.Sha256);
        Assert.NotNull(ended.SizeBytes);
        Assert.StartsWith("outputs/", ended.ObjectKey!, StringComparison.Ordinal);

        // The live data step committed the partition: its block count is the index's, and
        // the index's records are the partition's.
        var partition = await _stage.ReadPartitionAsync(composition, reportJobId, cancellationToken);
        var index = await _stage.ReadIndexAsync(composition, partition, cancellationToken);
        Assert.Equal(StreamingPartitioner.PartitionerVersion, partition.PartitionerVersion);
        Assert.Equal(index.Blocks.Count, partition.BlockCount);
        Assert.True(partition.BlockCount >= 2, "the live data step partitioned the snapshot into several blocks");
        Assert.Equal(index.Blocks.Sum(block => block.RecordCount), partition.RecordCount);
        Assert.Equal(index.Blocks.Sum(block => block.RecordBytes), partition.RecordBytes);

        // One artifact whose bytes verify through the storage port.
        var path = await _stage.DownloadAsync(
            composition,
            ended.ObjectKey!,
            ended.SizeBytes!.Value,
            ended.Sha256!,
            "live-multi-block.pdf",
            cancellationToken);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        Assert.Equal(ended.SizeBytes, bytes.LongLength);
        Assert.Equal(ended.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));

        var reading = PdfWordReader.Read(path, MultiBlockStage.Margins);
        var declaration = _stage.AccountingDeclaration;
        var records = _stage.AccountingRecords;
        Assert.True(records.Count >= 5_000, "the snapshot reaches the pilot dataset's group larger than a block");

        // The page attribution and the expected values come from the source records and the
        // document's own layout, never from the product's computation.
        var provisional = PageAccountingExpectations.Compute(
            records,
            new Dictionary<long, int>(),
            reading.Pages.Count,
            0m,
            declaration.Scale,
            declaration.Culture);
        var attribution = PageAccountingExpectations.Attribute(reading, provisional);
        var expected = PageAccountingExpectations.Compute(
            records,
            attribution.EndPages,
            reading.Pages.Count,
            0m,
            declaration.Scale,
            declaration.Culture);

        // Every record printed exactly once, its amount on the page it ended on, in the
        // snapshot's own order: the end pages never move backwards as the ordinals rise.
        Assert.Equal(records.Count, attribution.EndPages.Count);
        var endPagesInOrder = records
            .Select(record => attribution.EndPages[record.Ordinal])
            .ToList();
        Assert.Equal(endPagesInOrder.Order(), endPagesInOrder);

        // The accounting values, the group totals and the chain are exact on the assembled
        // distribution, and every page carries its one global stamp.
        var problems = AccountedDocumentCheck.Verify(reading, declaration, expected, attribution, expectNumbering: true);
        Assert.True(problems.Count == 0, string.Join("; ", problems));
        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"Pagina 1 de {reading.Pages.Count}"),
            string.Join(' ', PageAccountingExpectations.NumberingWords(reading.Pages[0], declaration)));

        // The page count is the committed parts' sum, and the parts are the delivered ones.
        var parts = await _stage.ReadPartsAsync(composition, ended.OutputId!, cancellationToken);
        Assert.True(parts.Count >= 2, "the committed partition holds more than one block");
        Assert.Equal(partition.BlockCount, parts.Count);
        Assert.Equal(parts.Sum(part => part.PageCount), reading.Pages.Count);
        Assert.All(parts, part => Assert.StartsWith("parts/", part.ObjectKey, StringComparison.Ordinal));

        // The grand total is the test's own sum, and the last page closes on it.
        Assert.Equal(records.Sum(record => record.Amount), expected.GrandTotal);
        Assert.Equal(expected.GrandTotal, expected.Pages[^1].CarriedForward);

        // The orchestration worker's log: it is read (it holds this output's publication
        // line), and it carries no single-block refusal and no start-up line.
        var log = await MultiBlockStage.ReadWorkerLogAsync(composition.App, "orchestration-worker", cancellationToken);
        Assert.Contains(log, line =>
            line.Contains("Output " + ended.OutputId + " published as " + ended.ArtifactId, StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("single-block", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(log, line => line.Contains("QUOTA_EXCEEDED", StringComparison.Ordinal));

        // The evidence stays with the run.
        var evidence = RunEnvironment.WriteEvidence("live-multi-block.pdf", bytes);
        RunEnvironment.WriteEvidence(
            "live-multi-block.evidence.txt",
            Encoding.UTF8.GetBytes(
                $"live-multi-block.pdf sha256={RunEnvironment.Sha256Of(bytes)} bytes={bytes.LongLength} "
                + $"blocks={partition.BlockCount} pages={reading.Pages.Count} records={records.Count} "
                + $"grandTotal={expected.GrandTotal.ToString(CultureInfo.InvariantCulture)}{Environment.NewLine}"));
        TestContext.Current.TestOutputHelper?.WriteLine($"live multi-block: {evidence} pages={reading.Pages.Count}");
    }
}
