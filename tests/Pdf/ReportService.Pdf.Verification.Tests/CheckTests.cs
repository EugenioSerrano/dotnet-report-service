using ReportService.Templates;

namespace ReportService.Pdf.Verification.Tests;

/// <summary>
/// The source-bound checks: the block against its partition, one row per record, a
/// printed unit per row, the declared fields at their values, and the pages a
/// document must have. Each case plants one defect and asserts the reason it is
/// reported with, and that no diagnostic carries the planted value.
/// </summary>
public sealed class CheckTests
{
    private static SourceCheckInput Input(
        ComposedBlock composition,
        IReadOnlyList<BlockRecord> records,
        IReadOnlyDictionary<string, object?>? header = null,
        VerificationAnchors? anchors = null,
        long indexCount = 2,
        long partitionCount = 2,
        bool isFirst = true) =>
        new(
            new PartitionFacts(1, partitionCount),
            new IndexFacts(0, indexCount, 1),
            new BlockFacts(0, 1, records, header ?? new Dictionary<string, object?>()),
            composition,
            new TextUnitExtractor().Extract(composition),
            anchors ?? VerificationAnchors.None,
            isFirst);

    private static BlockRecord Record(long ordinal, string description, string amount = "10.00") =>
        new(ordinal, new Dictionary<string, object?>
        {
            ["sequence"] = ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["description"] = description,
            ["amount"] = amount,
        });

    [Fact]
    public void ABlockThatAgreesWithItsPartitionAndItsCompositionPasses()
    {
        var composition = Sample.Rows("one", "two");
        var input = Input(composition, [Record(1, "one"), Record(2, "two")]);

        Assert.True(new RowCoverageCheck().CheckSource(input).Passed);
    }

    [Fact]
    public void AnIndexPageThatHoldsFewerRecordsThanThePartitionIsALostRow()
    {
        var composition = Sample.Rows("one");
        var input = Input(composition, [Record(1, "one")], indexCount: 1, partitionCount: 2);

        Assert.Equal(VerificationReasons.RowLost, new RowCoverageCheck().CheckSource(input).Reason);
    }

    [Fact]
    public void AnIndexPageThatHoldsMoreRecordsThanThePartitionIsAnExtraRow()
    {
        var composition = Sample.Rows("one", "two");
        var input = Input(composition, [Record(1, "one"), Record(2, "two")], indexCount: 2, partitionCount: 1);

        Assert.Equal(VerificationReasons.RowExtra, new RowCoverageCheck().CheckSource(input).Reason);
    }

    [Fact]
    public void ABlockThatHoldsOneRecordFewerThanTheIndexPageIsALostRow()
    {
        var composition = Sample.Rows("one", "two");
        var input = Input(composition, [Record(1, "one")], indexCount: 2, partitionCount: 2);

        Assert.Equal(VerificationReasons.RowLost, new RowCoverageCheck().CheckSource(input).Reason);
    }

    [Fact]
    public void AnOrdinalGapIsALostRow()
    {
        var composition = Sample.Rows("one", "two");
        var input = Input(composition, [Record(1, "one"), Record(3, "two")], indexCount: 2, partitionCount: 2);

        Assert.Equal(VerificationReasons.RowLost, new RowCoverageCheck().CheckSource(input).Reason);
    }

    [Fact]
    public void ARepeatedOrdinalIsAnExtraRow()
    {
        var composition = Sample.Rows("one", "two");
        var input = Input(composition, [Record(1, "one"), Record(1, "two")], indexCount: 2, partitionCount: 2);

        Assert.Equal(VerificationReasons.RowExtra, new RowCoverageCheck().CheckSource(input).Reason);
    }

    [Fact]
    public void AReorderedPairIsARowOrderDefect()
    {
        var composition = Sample.Rows("one", "two");
        var input = Input(composition, [Record(2, "two"), Record(1, "one")], indexCount: 2, partitionCount: 2);

        Assert.Equal(VerificationReasons.RowOrder, new RowCoverageCheck().CheckSource(input).Reason);
    }

    [Fact]
    public void ACompositionThatOmitsARowIsALostRow()
    {
        var composition = Sample.Rows("one");
        var input = Input(composition, [Record(1, "one"), Record(2, "two")]);

        Assert.Equal(VerificationReasons.RowLost, new RowCoverageCheck().CheckSource(input).Reason);
    }

    [Fact]
    public void ACompositionThatPrintsARowTwiceIsAnExtraRow()
    {
        var composition = Sample.Rows("one", "two", "two");
        var input = Input(composition, [Record(1, "one"), Record(2, "two")]);

        Assert.Equal(VerificationReasons.RowExtra, new RowCoverageCheck().CheckSource(input).Reason);
    }

    [Fact]
    public void ACompositionWhoseRowsAreOutOfOrderIsARowOrderDefect()
    {
        var composition = Sample.Block(
            (FragmentKind.DocumentStart, 0, "<p>title</p>"),
            (FragmentKind.Row, 2, "<p>two</p>"),
            (FragmentKind.Row, 1, "<p>one</p>"),
            (FragmentKind.DocumentEnd, 0, "<p>end</p>"));
        var input = Input(composition, [Record(1, "one"), Record(2, "two")]);

        Assert.Equal(VerificationReasons.RowOrder, new RowCoverageCheck().CheckSource(input).Reason);
    }

    [Fact]
    public void ARowWhoseTextWasNotPrintedIsALostRow()
    {
        var composition = Sample.Rows("one", "two");
        var input = Input(composition, [Record(1, "one"), Record(2, "two")]);
        var consumed = input.Text.Units.Select(_ => 0).ToList();

        Assert.Equal(VerificationReasons.RowLost, new RowCoverageCheck().CheckConsumed(composition, input.Text, consumed).Reason);
    }

    [Fact]
    public void EveryRowWhoseTextWasPrintedPassesTheConsumedCheck()
    {
        var composition = Sample.Rows("one", "two");
        var input = Input(composition, [Record(1, "one"), Record(2, "two")]);
        var consumed = input.Text.Units.Select(unit => unit.Normalised.Length).ToList();

        Assert.True(new RowCoverageCheck().CheckConsumed(composition, input.Text, consumed).Passed);
    }

    [Fact]
    public void ARowThatPrintsNoTextIsReportedAsSuch()
    {
        var composition = Sample.Block(
            (FragmentKind.DocumentStart, 0, "<p>title</p>"),
            (FragmentKind.Row, 1, "<p> </p>"),
            (FragmentKind.DocumentEnd, 0, "<p>end</p>"));
        var input = Input(composition, [Record(1, "one")], indexCount: 1, partitionCount: 1);

        Assert.Equal(VerificationReasons.RowWithoutText, new RowTextCheck().Check(input).Reason);
    }

    [Fact]
    public void ARowThatPrintsTextPassesTheRowTextCheck()
    {
        var composition = Sample.Rows("one");
        var input = Input(composition, [Record(1, "one")], indexCount: 1, partitionCount: 1);

        Assert.True(new RowTextCheck().Check(input).Passed);
    }

    [Fact]
    public void ADeclaredItemFieldAtItsValuePasses()
    {
        var composition = Sample.Rows("one|10.00", "two|20.00");
        var input = Input(
            composition,
            [Record(1, "one", "10.00"), Record(2, "two", "20.00")],
            anchors: new VerificationAnchors([], ["$.description", "$.amount"]));

        Assert.True(new AnchorCheck().Check(input).Passed);
    }

    [Fact]
    public void ATruncatedFieldIsReportedWithItsPath()
    {
        var composition = Sample.Rows("on|10.00", "two|20.00");
        var input = Input(
            composition,
            [Record(1, "one", "10.00"), Record(2, "two", "20.00")],
            anchors: new VerificationAnchors([], ["$.description"]));

        var outcome = new AnchorCheck().Check(input);

        Assert.Equal(VerificationReasons.AnchorMismatch, outcome.Reason);
        Assert.Equal("$.description", outcome.AnchorPath);
        Assert.Equal(1, outcome.RecordOrdinal);
        Assert.DoesNotContain("one", outcome.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void AValuePrintedOnlyInAnotherRecordsRowIsReported()
    {
        var composition = Sample.Rows("one|10.00", "one|20.00");
        var input = Input(
            composition,
            [Record(1, "one", "10.00"), Record(2, "two", "20.00")],
            anchors: new VerificationAnchors([], ["$.description"]));

        Assert.Equal(VerificationReasons.AnchorMismatch, new AnchorCheck().Check(input).Reason);
    }

    [Fact]
    public void AValueThatIsNotTextIsNotChecked()
    {
        var composition = Sample.Rows("one");
        var records = new List<BlockRecord>
        {
            new(1, new Dictionary<string, object?>
            {
                ["missing"] = null,
                ["flag"] = true,
                ["object"] = new Dictionary<string, object?>(),
                ["array"] = new List<object?>(),
                ["description"] = "one",
            }),
        };
        var input = Input(
            composition,
            records,
            anchors: new VerificationAnchors([], ["$.missing", "$.flag", "$.object", "$.array", "$.description"]),
            indexCount: 1,
            partitionCount: 1);

        Assert.True(new AnchorCheck().Check(input).Passed);
    }

    [Fact]
    public void ANumberGivenAsTextIsChecked()
    {
        var composition = Sample.Rows("one|10.00");
        var records = new List<BlockRecord>
        {
            new(1, new Dictionary<string, object?> { ["description"] = "one", ["amount"] = "10.01" }),
        };
        var input = Input(composition, records, anchors: new VerificationAnchors([], ["$.amount"]), indexCount: 1, partitionCount: 1);

        Assert.Equal(VerificationReasons.AnchorMismatch, new AnchorCheck().Check(input).Reason);
    }

    [Fact]
    public void AValueWithMarkupCharactersAndAccentsIsFoundWhenTheTextIsDecoded()
    {
        const string value = "Tom & Jerry \"quoted\" <b>bold</b> é ñ";
        var composition = Sample.Block((FragmentKind.Row, 1, "<p>Tom &amp; Jerry &quot;quoted&quot; &lt;b&gt;bold&lt;/b&gt; é ñ</p>"));
        var records = new List<BlockRecord>
        {
            new(1, new Dictionary<string, object?> { ["description"] = value }),
        };
        var input = Input(composition, records, anchors: new VerificationAnchors([], ["$.description"]), indexCount: 1, partitionCount: 1);

        Assert.True(new AnchorCheck().Check(input).Passed);
    }

    [Fact]
    public void AHeaderFieldIsCheckedInTheDocumentParts()
    {
        var composition = Sample.Block(
            (FragmentKind.DocumentStart, 0, "<h1>Statement</h1>"),
            (FragmentKind.Row, 1, "<p>one</p>"),
            (FragmentKind.DocumentEnd, 0, "<p>end</p>"));
        var input = Input(
            composition,
            [Record(1, "one")],
            header: new Dictionary<string, object?> { ["reportTitle"] = "Statement" },
            anchors: new VerificationAnchors(["$.reportTitle"], []),
            indexCount: 1,
            partitionCount: 1);

        Assert.True(new AnchorCheck().Check(input).Passed);
    }

    [Fact]
    public void AHeaderFieldIsNotCheckedOutsideTheFirstBlock()
    {
        var composition = Sample.Block(
            (FragmentKind.DocumentStart, 0, "<h1>Other</h1>"),
            (FragmentKind.Row, 1, "<p>one</p>"),
            (FragmentKind.DocumentEnd, 0, "<p>end</p>"));
        var input = Input(
            composition,
            [Record(1, "one")],
            header: new Dictionary<string, object?> { ["reportTitle"] = "Statement" },
            anchors: new VerificationAnchors(["$.reportTitle"], []),
            indexCount: 1,
            partitionCount: 1,
            isFirst: false);

        Assert.True(new AnchorCheck().Check(input).Passed);
    }

    [Fact]
    public void APackageThatDeclaresNoFieldFailsClosed()
    {
        var composition = Sample.Rows("one");
        var input = new SourceCheckInput(
            new PartitionFacts(1, 1),
            new IndexFacts(0, 1, 1),
            new BlockFacts(0, 1, [Record(1, "one")], new Dictionary<string, object?>()),
            composition,
            new TextUnitExtractor().Extract(composition),
            Anchors: null,
            IsFirstBlock: true);

        Assert.Equal(VerificationReasons.AnchorsUndeclared, new AnchorCheck().Check(input).Reason);
    }

    [Fact]
    public void ADocumentWithNoPageIsRefused()
    {
        Assert.Equal(VerificationReasons.NoPage, new PageCheck().Check(new PdfReading([])).Reason);
    }

    [Fact]
    public void APageThatPrintsNoWordIsBlank()
    {
        var reading = new PdfReading([new PrintedPage(1, 595, 842, []), new PrintedPage(2, 595, 842, [Word("x")])]);

        Assert.Equal(VerificationReasons.BlankPage, new PageCheck().Check(reading).Reason);
    }

    [Fact]
    public void APageThatPrintsOnlyABandWordIsNotBlank()
    {
        var reading = new PdfReading([new PrintedPage(1, 595, 842, [Word("header", ReadingBand.Header)])]);

        Assert.True(new PageCheck().Check(reading).Passed);
    }

    private static PrintedWord Word(string text, ReadingBand band = ReadingBand.Content) =>
        new(text, 10, 20, 30, 30, 0, band);
}
