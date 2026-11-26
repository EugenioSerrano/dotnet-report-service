using ReportService.Templates;

namespace ReportService.Pdf.Verification.Tests;

/// <summary>
/// The row coverage of a block that is not the first: the block document numbers its
/// records locally, but the index page declares the snapshot's global interval. A
/// composition that carries the global ordinals passes; one that leaks the
/// block-local ordinals is refused as an extra row, which is the defect the render
/// step's read had to reconcile.
/// </summary>
public sealed class RowCoverageOrdinalTests
{
    [Fact]
    public void ASecondBlockWhoseRecordsAndCompositionCarryTheGlobalIntervalPasses()
    {
        var composition = Composition();
        var input = Input(composition, [Record(4), Record(5), Record(6)]);

        var outcome = new RowCoverageCheck().CheckSource(input);

        Assert.True(outcome.Passed, outcome.Reason);
    }

    [Fact]
    public void TheGlobalIntervalStartsAndEndsOnTheOrdinalsTheIndexDeclares()
    {
        var input = Input(Composition(), [Record(4), Record(5), Record(6)]);

        Assert.Equal(4, input.Index.FirstRecordOrdinal);
        Assert.Equal(input.Index.FirstRecordOrdinal, input.Block.Records[0].Ordinal);
        Assert.Equal(
            input.Index.FirstRecordOrdinal + input.Index.RecordCount - 1,
            input.Block.Records[^1].Ordinal);
        Assert.Equal(
            input.Index.FirstRecordOrdinal + input.Index.RecordCount - 1,
            input.Composition.Rows.Last().RecordOrdinal);
    }

    [Fact]
    public void ASecondBlockWhoseRecordsCarryTheBlockLocalOrdinalsIsRefusedAsAnExtraRow()
    {
        // The defect the render step had: the block document's local ordinals 1..3 are
        // compared with the index's global interval 4..6, so the first record is outside
        // the interval and the block is refused before anything is printed.
        var input = Input(Composition(), [Record(1), Record(2), Record(3)]);

        var outcome = new RowCoverageCheck().CheckSource(input);

        Assert.False(outcome.Passed);
        Assert.Equal(VerificationReasons.RowExtra, outcome.Reason);
        Assert.Equal(1, outcome.RecordOrdinal);
    }

    private static ComposedBlock Composition() => Sample.Block(
        (FragmentKind.DocumentStart, 0, "<p>title</p>"),
        (FragmentKind.Row, 4, "<p>one</p>"),
        (FragmentKind.Row, 5, "<p>two</p>"),
        (FragmentKind.Row, 6, "<p>three</p>"),
        (FragmentKind.DocumentEnd, 0, "<p>end</p>"));

    private static SourceCheckInput Input(ComposedBlock composition, IReadOnlyList<BlockRecord> records) =>
        new(
            new PartitionFacts(2, 6),
            new IndexFacts(1, 3, 4),
            new BlockFacts(1, 4, records, new Dictionary<string, object?>()),
            composition,
            new TextUnitExtractor().Extract(composition),
            VerificationAnchors.None,
            IsFirstBlock: false);

    private static BlockRecord Record(long ordinal) => new(
        ordinal,
        new Dictionary<string, object?>
        {
            ["sequence"] = ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["description"] = "record " + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["amount"] = "10.00",
        });
}
