namespace ReportService.PdfOracle.Tests;

/// <summary>
/// The extraction-level controls: every change planted in the words of a document
/// that passes is reported with the failure class it plants. The judge is trusted
/// on a class only while its control behaves.
/// </summary>
public sealed class ExtractionControlTests
{
    [Fact]
    public void EveryControlIsReportedWithTheFailureItPlants()
    {
        var outcomes = ExtractionControls.Run(SyntheticDocument.Extraction(), SyntheticDocument.Fixture(), SyntheticDocument.Expectation());

        Assert.Equal(8, outcomes.Count);
        foreach (var outcome in outcomes)
        {
            Assert.True(
                outcome.Behaved,
                $"{outcome.Id} planted {outcome.Expected} and the judge reported: {string.Join("; ", outcome.Observed)}");
        }
    }

    [Theory]
    [InlineData("E1", "text-dropped")]
    [InlineData("E3", "row-lost")]
    [InlineData("E4", "text-altered")]
    [InlineData("E5", "row-order")]
    [InlineData("E6", "row-duplicated")]
    [InlineData("E7", "structure-lost")]
    [InlineData("E8", "row-order")]
    public void TheControlForEachClassIsReportedAsThatClass(string id, string expected)
    {
        var outcomes = ExtractionControls.Run(SyntheticDocument.Extraction(), SyntheticDocument.Fixture(), SyntheticDocument.Expectation());

        var outcome = Assert.Single(outcomes, candidate => candidate.Id == id);
        Assert.Equal(expected, outcome.Expected);
        Assert.True(outcome.Behaved, $"{id} planted {expected} and the judge reported: {string.Join("; ", outcome.Observed)}");
    }

    [Fact]
    public void ARepeatedLineIsReportedAsRepeatedText()
    {
        var outcomes = ExtractionControls.Run(SyntheticDocument.Extraction(), SyntheticDocument.Fixture(), SyntheticDocument.Expectation());

        var outcome = Assert.Single(outcomes, candidate => candidate.Id == "E2");
        Assert.Equal("text-repeated", outcome.Expected);
        Assert.True(outcome.Behaved, $"E2 planted text-repeated and the judge reported: {string.Join("; ", outcome.Observed)}");
    }

    [Fact]
    public void TheControlsActOnADocumentThatPassesAndSaySoWhenTheyCannot()
    {
        var words = JudgeTests.Without(SyntheticDocument.Extraction().Words, word => word.Text is "Pilot" or "report");

        var outcomes = ExtractionControls.Run(SyntheticDocument.Extraction().WithWords(words), SyntheticDocument.Fixture(), SyntheticDocument.Expectation());

        var outcome = Assert.Single(outcomes);
        Assert.Equal("base", outcome.Id);
        Assert.False(outcome.Behaved);
        Assert.NotEmpty(outcome.Observed);
    }
}
