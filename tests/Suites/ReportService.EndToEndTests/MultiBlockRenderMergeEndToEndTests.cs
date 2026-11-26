using System.Globalization;
using ReportService.Pdf.Verification;

namespace ReportService.EndToEndTests;

/// <summary>
/// The multi-block render, ordered merge and global numbering over the whole local
/// composition: a committed multi-block partition for an accepted job, the real
/// Chromium worker rendering the blocks, the real merge worker assembling them in
/// manifest order and stamping one global "Page X of Y", and the published PDF read
/// back and checked page by page against the parts' own commits. The template
/// prints its own page counter, so the assembled document also proves the counter
/// was suppressed. Authored in the V-Bounce; run in the e2e cycle.
/// </summary>
public sealed class MultiBlockRenderMergeEndToEndTests : IDisposable
{
    private readonly MultiBlockStage _stage = new();

    /// <inheritdoc />
    public void Dispose() => _stage.Dispose();

    [Fact]
    public async Task TheCommittedBlocksRenderAndMergeInManifestOrderWithOneGlobalStamp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _stage.StartAsync(cancellationToken);
        await _stage.TakeTemplateAsync(composition, cancellationToken);
        var uploadId = await _stage.SeedConfirmingUploadAsync(composition, cancellationToken);
        var reportJobId = await _stage.SubmitAsync(composition, uploadId, cancellationToken);
        await _stage.CommitPartitionAsync(composition, reportJobId, uploadId, cancellationToken);
        await _stage.CompleteUploadAsync(composition, uploadId, cancellationToken);

        var ended = await _stage.WaitForEndAsync(composition, reportJobId, cancellationToken);

        Assert.Equal("SUCCEEDED", ended.Status);
        Assert.NotNull(ended.ArtifactId);
        Assert.NotNull(ended.OutputId);
        Assert.NotNull(ended.ObjectKey);
        Assert.NotNull(ended.Sha256);
        Assert.NotNull(ended.SizeBytes);
        Assert.StartsWith("outputs/", ended.ObjectKey!, StringComparison.Ordinal);

        var path = await _stage.DownloadAsync(
            composition,
            ended.ObjectKey!,
            ended.SizeBytes!.Value,
            ended.Sha256!,
            "assembled.pdf",
            cancellationToken);
        var assembled = PdfWordReader.Read(path, MultiBlockStage.Margins);
        var area = MultiBlockStage.Area;

        // One continuous global stamp on every page, in the declared area, and no
        // part-local counter anywhere in the assembled document.
        for (var index = 0; index < assembled.Pages.Count; index++)
        {
            var stamp = string.Join(
                ' ',
                assembled.Pages[index].Words
                    .Where(word => MultiBlockStage.Inside(word, area))
                    .Select(word => word.Text));
            Assert.Equal(
                string.Create(CultureInfo.InvariantCulture, $"Page {index + 1} of {assembled.Pages.Count}"),
                stamp);
            Assert.DoesNotContain(
                "PART-COUNTER",
                assembled.Pages[index].Words.Select(word => word.Text),
                StringComparer.Ordinal);
        }

        // The parts' own commits: the page count is their sum, every part is a parts/…
        // object, and every output page carries the same text outside the area as its
        // source page, in manifest order.
        var parts = await _stage.ReadPartsAsync(composition, ended.OutputId!, cancellationToken);
        Assert.True(parts.Count >= 2, "the partition holds more than one block");
        Assert.Equal(parts.Sum(part => part.PageCount), assembled.Pages.Count);
        Assert.All(parts, part => Assert.StartsWith("parts/", part.ObjectKey, StringComparison.Ordinal));

        var page = 0;
        foreach (var part in parts)
        {
            var partPath = await _stage.DownloadAsync(
                composition,
                part.ObjectKey,
                part.SizeBytes,
                part.Sha256,
                $"part-{part.BlockIndex}.pdf",
                cancellationToken);
            var source = PdfWordReader.Read(partPath, MultiBlockStage.Margins);
            Assert.Equal(part.PageCount, source.Pages.Count);
            foreach (var sourcePage in source.Pages)
            {
                Assert.Equal(
                    MultiBlockStage.OutsideArea(sourcePage),
                    MultiBlockStage.OutsideArea(assembled.Pages[page]));
                page++;
            }
        }

        Assert.Equal(assembled.Pages.Count, page);
    }
}
