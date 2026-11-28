using System.Text;
using System.Text.Json.Nodes;
using ReportService.Testing.Fixtures;

namespace ReportService.EndToEndTests;

/// <summary>
/// The record bound, the placement of a record above one mebibyte and the PDF row bound of an
/// image, over the whole local composition: a record of about 1.5 MiB carrying a 1 MiB PNG data
/// URI is placed alone in its block and printed; an image one byte over its bound, an ordinary
/// text value one byte over its bound and a record one byte over the record bound are refused
/// with their stable codes, the row-bound refusals naming the bound that applied.
/// </summary>
public sealed class LargeRecordPdfEndToEndTests
{
    private const int ImageBound = 1_048_576;
    private const int OrdinaryBound = 262_144;
    private const int RecordBound = 2_097_152;

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Printing the image needs the template to place a data value in an image source, which
    /// the image-source rule admits; the class is marked so the covering run executes it once
    /// that rule is delivered.
    /// </summary>
    [Fact]
    [Trait("Requires", "image-source")]
    [Trait("Requires", "printing")]
    public async Task ARecordWithAOneMebibyteImageIsAloneInItsBlockAndPrinted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var image = ImageDataUri(ImageBound);
        using var stage = new MultiBlockStage(
            accounted: true,
            templateId: "large-record-image",
            snapshotFactory: _ => Snapshot(
                Item(1, "before", null),
                Item(2, "the image", image, padding: 400_000),
                Item(3, "after", null)),
            packageTransform: files =>
            {
                files["html/row.html.sbn"] = Encoding.UTF8.GetBytes(
                    """<div class="item"><div class="seq">{{ block.recordOrdinal }}</div><div class="desc">{{ item.description | html.escape }}</div>{{ if item.picture }}<img alt="" src="{{ item.picture }}">{{ end }}<div class="amount" data-role="amount">{{ item.amount | html.escape }}</div></div>""");
                return files;
            });
        await using var composition = await stage.StartAsync(cancellationToken);
        await stage.TakeTemplateAsync(composition, cancellationToken);
        var uploadId = await stage.SeedConfirmingUploadAsync(composition, cancellationToken, 3);
        var reportJobId = await stage.SubmitAsync(composition, uploadId, cancellationToken);
        await stage.CompleteUploadAsync(composition, uploadId, cancellationToken);

        var ended = await stage.WaitForEndAsync(composition, reportJobId, cancellationToken, Deadline);

        Assert.Equal("SUCCEEDED", ended.Status);
        Assert.Equal("SUCCEEDED", ended.OutputStatus);
        Assert.NotNull(ended.ArtifactId);

        var partition = await stage.ReadPartitionAsync(composition, reportJobId, cancellationToken);
        var index = await stage.ReadIndexAsync(composition, partition, cancellationToken);
        Assert.Equal(3, index.Blocks.Count);
        Assert.Equal(1, index.Blocks[1].RecordCount);
        Assert.True(index.Blocks[1].RecordBytes > 1_048_576, "the image's record is above one mebibyte");
    }

    [Fact]
    public async Task AnImageOneByteOverItsBoundRefusesThePdfNamingTheImageBound()
    {
        var ended = await RunAsync(
            "large-record-image-over",
            Snapshot(Item(1, "image", ImageDataUri(ImageBound + 1))),
            TestContext.Current.CancellationToken);

        Assert.Equal("ROW_BOUND_EXCEEDED", ended.OutputErrorCode);
        Assert.Contains("size=1048577 bound=1048576", ended.OutputErrorReason, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image", ended.OutputErrorReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrdinaryTextOneByteOverItsBoundRefusesThePdfNamingTheOrdinaryBound()
    {
        var ended = await RunAsync(
            "large-record-text-over",
            Snapshot(Item(1, new string('a', OrdinaryBound + 1), null)),
            TestContext.Current.CancellationToken);

        Assert.Equal("ROW_BOUND_EXCEEDED", ended.OutputErrorCode);
        Assert.Contains("size=262145 bound=262144", ended.OutputErrorReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecordOneByteOverTheRecordBoundFailsTheJobAsRecordTooLarge()
    {
        var ended = await RunAsync(
            "large-record-over",
            Snapshot(Item(1, "wide", null, padding: RecordBound)),
            TestContext.Current.CancellationToken);

        Assert.Equal("FAILED", ended.Status);
        Assert.Equal("RECORD_TOO_LARGE", ended.ErrorCode);
    }

    private static async Task<MultiBlockStage.EndedJob> RunAsync(
        string templateId,
        (byte[] Snapshot, IReadOnlyList<AccountedRecord> Records) snapshot,
        CancellationToken cancellationToken)
    {
        using var stage = new MultiBlockStage(accounted: true, templateId: templateId, snapshotFactory: _ => snapshot);
        await using var composition = await stage.StartAsync(cancellationToken);
        await stage.TakeTemplateAsync(composition, cancellationToken);
        var uploadId = await stage.SeedConfirmingUploadAsync(composition, cancellationToken, snapshot.Records.Count);
        var reportJobId = await stage.SubmitAsync(composition, uploadId, cancellationToken);
        await stage.CompleteUploadAsync(composition, uploadId, cancellationToken);
        return await stage.WaitForEndAsync(composition, reportJobId, cancellationToken, Deadline);
    }

    private static (byte[] Snapshot, IReadOnlyList<AccountedRecord> Records) Snapshot(params JsonObject[] items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(item);
        }

        var snapshot = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["header"] = new JsonObject { ["reportTitle"] = "Large records", ["currency"] = "ARS" },
            ["items"] = array,
        };
        var records = items
            .Select(item => new AccountedRecord(item["sequence"]!.GetValue<int>(), "g", 1.00m))
            .ToList();
        return (Encoding.UTF8.GetBytes(snapshot.ToJsonString()), records);
    }

    private static JsonObject Item(int sequence, string description, string? picture, int padding = 0)
    {
        var item = new JsonObject
        {
            ["sequence"] = sequence,
            ["groupId"] = "g",
            ["description"] = description,
            ["amount"] = 1.00m,
        };
        if (picture is not null)
        {
            item["picture"] = picture;
        }

        if (padding > 0)
        {
            // Padding in short values, so only the record bound - never the row bound - is crossed.
            for (var index = 0; index * 100_000 < padding; index++)
            {
                item["pad" + index] = new string('p', 100_000);
            }
        }

        return item;
    }

    private static string ImageDataUri(int bytes)
    {
        for (var pad = 1; pad <= 4; pad++)
        {
            var prefix = "data:image/png;n=" + new string('p', pad) + ";base64,";
            if ((bytes - prefix.Length) % 4 == 0)
            {
                return prefix + new string('A', bytes - prefix.Length);
            }
        }

        throw new InvalidOperationException("four consecutive prefix lengths cover every remainder");
    }
}
