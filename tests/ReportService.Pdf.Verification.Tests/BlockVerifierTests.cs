using ReportService.Templates;

namespace ReportService.Pdf.Verification.Tests;

/// <summary>
/// The verifier: the order of the checks, that a source failure ends the block with
/// nothing printed, and that a document the reader cannot read fails instead of
/// passing as empty.
/// </summary>
public sealed class BlockVerifierTests
{
    private static SourceCheckInput Input(VerificationAnchors? anchors, long indexCount = 1)
    {
        var composition = Sample.Rows("one");
        return new SourceCheckInput(
            new PartitionFacts(1, indexCount),
            new IndexFacts(0, indexCount, 1),
            new BlockFacts(0, 1, [new BlockRecord(1, new Dictionary<string, object?> { ["description"] = "one" })], new Dictionary<string, object?>()),
            composition,
            new TextUnitExtractor().Extract(composition),
            anchors,
            IsFirstBlock: true);
    }

    [Fact]
    public void AConsistentBlockPassesTheSourceChecks()
    {
        var outcome = new BlockVerifier().CheckSource(Input(new VerificationAnchors([], ["$.description"])), out var elapsed);

        Assert.True(outcome.Passed);
        Assert.True(elapsed >= 0);
    }

    [Fact]
    public void TheDeclaredFieldsAreCheckedBeforeTheRest()
    {
        // The block lacks a record and declares no field: an undeclared package is a
        // refusal of the block, whatever else would have failed as well.
        var outcome = new BlockVerifier().CheckSource(Input(anchors: null, indexCount: 2), out _);

        Assert.Equal(VerificationReasons.AnchorsUndeclared, outcome.Reason);
    }

    [Fact]
    public void ABlockWithoutItsRecordsFailsBeforePrinting()
    {
        var outcome = new BlockVerifier().CheckSource(Input(new VerificationAnchors([], []), indexCount: 2), out _);

        Assert.Equal(VerificationReasons.RowLost, outcome.Reason);
    }

    [Fact]
    public void ADocumentTheReaderCannotReadFailsAsUnreadable()
    {
        var composition = Sample.Rows("one");
        var result = new BlockVerifier().Verify(new PrintedCheckInput(
            Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".pdf"),
            0,
            PageMargins.None,
            composition,
            new TextUnitExtractor().Extract(composition),
            SourceChecksMs: 3));

        Assert.False(result.Passed);
        Assert.Equal(VerificationReasons.Unreadable, result.Reason);
        Assert.Equal(0, result.Pages);
        Assert.Equal(3, result.SourceChecksMs);
    }

    [Fact]
    public void ADocumentThatDoesNotHoldTheComposedTextFails()
    {
        var composition = Sample.Rows("a text this document does not print");
        var path = Path.Combine(AppContext.BaseDirectory, "PdfCases", "s2-table-s2-T300u-split-t1.00-f0.90-on-r1.pdf");

        var result = new BlockVerifier().Verify(new PrintedCheckInput(
            path,
            0,
            PlacementConvention.FromMillimetres(new PageMarginsMm(15, 12, 18, 10)),
            composition,
            new TextUnitExtractor().Extract(composition),
            SourceChecksMs: 1));

        Assert.False(result.Passed);
        Assert.NotNull(result.Reason);
        Assert.True(result.Pages > 0);
        Assert.True(result.Words > 0);
        Assert.Null(result.RecordOrdinal);
        Assert.Null(result.AnchorPath);
    }

    [Fact]
    public void ADocumentWithNoPageFailsBeforeAnythingIsCompared()
    {
        var composition = Sample.Rows("one");
        var verifier = new BlockVerifier();
        var result = verifier.Verify(new PrintedCheckInput(
            Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".pdf"),
            2,
            PageMargins.None,
            composition,
            new TextUnitExtractor().Extract(composition),
            SourceChecksMs: 0));

        Assert.False(result.Passed);
        Assert.Equal(2, result.BlockIndex);
    }
}
