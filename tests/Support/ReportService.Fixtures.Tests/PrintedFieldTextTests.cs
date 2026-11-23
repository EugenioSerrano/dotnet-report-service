using ReportService.Pdf.Verification;
using ReportService.Testing.Reading;

namespace ReportService.Fixtures.Tests;

/// <summary>
/// The independent reading: the source appears as one contiguous run of the printed
/// content sequence, and a difference is reported by class and position, never by a
/// character value. The unit cases use synthetic word sequences, never a product file.
/// </summary>
public sealed class PrintedFieldTextTests
{
    [Fact]
    public void AValueAtTheHeadTheMiddleAndTheTailIsFound()
    {
        Assert.True(Reading(["abcdef"]).Find("abcdef").Passed);
        Assert.True(Reading(["xx", "abcdef", "yy"]).Find("abcdef").Passed);
        Assert.True(Reading(["xx", "yy", "abcdef"]).Find("abcdef").Passed);
    }

    [Fact]
    public void TheMatchedLengthIsTheNormalisedSourceLength()
    {
        var match = Reading(["xx", "abcdef", "yy"]).Find("abc def");

        Assert.True(match.Passed);
        Assert.Equal(6, match.Length);
        Assert.Equal(2, match.Index);
    }

    [Fact]
    public void AMissingCharacterIsReportedAtItsPosition()
    {
        var match = Reading(["abcdefhij"]).Find("abcdefghij");

        Assert.False(match.Passed);
        Assert.Equal(FieldDifference.Missing, match.Difference);
        Assert.Equal(6, match.Position);
    }

    [Fact]
    public void AnExtraCharacterIsReportedAtItsPosition()
    {
        var match = Reading(["abcdefghij"]).Find("abcdefhij");

        Assert.False(match.Passed);
        Assert.Equal(FieldDifference.Extra, match.Difference);
        Assert.Equal(6, match.Position);
    }

    [Fact]
    public void ATransposedPairIsReportedAsAlteredAtItsPosition()
    {
        var match = Reading(["abdcefghij"]).Find("abcdefghij");

        Assert.False(match.Passed);
        Assert.Equal(FieldDifference.Altered, match.Difference);
        Assert.Equal(2, match.Position);
    }

    [Fact]
    public void AReplacementCharacterIsReportedAtItsPosition()
    {
        var match = Reading(["abcde\ufffdghij"]).Find("abcdefghij");

        Assert.False(match.Passed);
        Assert.Equal(FieldDifference.Replacement, match.Difference);
        Assert.Equal(5, match.Position);
    }

    [Fact]
    public void ATruncatedTailIsReportedAtTheEndOfThePrintedText()
    {
        var match = Reading(["abcdefg"]).Find("abcdefghij");

        Assert.False(match.Passed);
        Assert.Equal(FieldDifference.Truncated, match.Difference);
        Assert.Equal(7, match.Position);
    }

    [Fact]
    public void AnEmptySourcePassesWithNoLength()
    {
        var match = Reading(["abc"]).Find(string.Empty);

        Assert.True(match.Passed);
        Assert.Equal(0, match.Length);
    }

    [Fact]
    public void ASourceLongerThanThePrintedTextIsTruncated()
    {
        var match = Reading(["abcde"]).Find("abcdefghij");

        Assert.False(match.Passed);
        Assert.Equal(FieldDifference.Truncated, match.Difference);
        Assert.Equal(5, match.Position);
    }

    [Fact]
    public void AValueSpanningPagesReportsBothPages()
    {
        var match = Reading(new[] { new[] { "abc" }, new[] { "def" } }).Find("abcdef");

        Assert.True(match.Passed);
        Assert.Equal(1, match.FirstPage);
        Assert.Equal(2, match.LastPage);
    }

    [Fact]
    public void TheRepeatedHeadIsDroppedOnAContinuationPage()
    {
        var reading = Reading(new[] { new[] { "prefix" }, new[] { "#", "Description", "abcdef" } }, ["#", "Description"]);

        var match = reading.Find("abcdef");

        Assert.True(match.Passed);
        Assert.Equal(6, match.Index);
        Assert.Equal(2, match.FirstPage);
        Assert.Equal(2, match.LastPage);
    }

    [Fact]
    public void TheHeaderAndFooterBandsAreNotCompared()
    {
        var page = new PrintedPage(1, 595, 842,
        [
            new PrintedWord("abc", 0, 0, 0, 0, 0, ReadingBand.Content),
            new PrintedWord("noise", 0, 0, 0, 0, 1, ReadingBand.Header),
            new PrintedWord("def", 0, 0, 0, 0, 2, ReadingBand.Content),
            new PrintedWord("foot", 0, 0, 0, 0, 3, ReadingBand.Footer),
        ]);

        var match = new PrintedFieldReading(new PdfReading([page]), []).Find("abcdef");

        Assert.True(match.Passed);
        Assert.Equal(6, match.Length);
    }

    private static PrintedFieldReading Reading(params string[] words) => Reading([words]);

    private static PrintedFieldReading Reading(IReadOnlyList<string[]> pages, IReadOnlyList<string>? headRow = null)
    {
        var printed = pages
            .Select((words, index) => new PrintedPage(
                index + 1,
                595,
                842,
                [.. words.Select((text, position) => new PrintedWord(text, 0, 0, 0, 0, position, ReadingBand.Content))]))
            .ToList();
        return new PrintedFieldReading(new PdfReading(printed), headRow ?? []);
    }
}
