using System.Globalization;
using System.Text;

namespace ReportService.PdfOracle.Tests;

/// <summary>The reading of the text tool's word output: pages, sizes, words and the boxes of each.</summary>
public sealed class BboxTextTests
{
    private const string Output = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Transitional//EN" "http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd">
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title></title></head>
        <body>
        <doc>
        <page width="595.276000" height="841.890000">
        <flow><block xMin="34.015747" yMin="45.000000" xMax="561.259827" yMax="57.000000">
        <line xMin="34.015747" yMin="45.000000" xMax="95.000000" yMax="57.000000">
        <word xMin="34.015747" yMin="45.000000" xMax="60.000000" yMax="57.000000">Pilot</word>
        <word xMin="62.000000" yMin="45.000000" xMax="95.000000" yMax="57.000000">A&amp;B</word>
        </line></block></flow></page>
        </doc>
        </body></html>
        """;

    [Fact]
    public void TheOutputIsReadPageByPageWithEveryBox()
    {
        var extraction = BboxText.Parse(Stream(Output), reportedPages: 1);

        Assert.Equal(1, extraction.Pages);
        Assert.Equal(1, extraction.ReportedPages);
        Assert.Equal(595.276, extraction.PageSizes[0].Width, 3);
        Assert.Equal(841.89, extraction.PageSizes[0].Height, 3);
        Assert.Equal(["Pilot", "A&B"], extraction.Words.Select(word => word.Text));
        Assert.Equal(1, extraction.Words[0].Page);
        Assert.Equal(34.015747, extraction.Words[0].XMin, 6);
        Assert.Equal(57.0, extraction.Words[0].YMax, 6);
        Assert.Equal((60.0 + 34.015747) / 2, extraction.Words[0].XCentre, 6);
    }

    [Fact]
    public void TwoPagesAreCountedFromTheOutputEvenWhenTheFileReportsAnotherCount()
    {
        var twoPages = Output.Replace("</doc>", "<page width=\"595.276000\" height=\"841.890000\"></page></doc>", StringComparison.Ordinal);

        var extraction = BboxText.Parse(Stream(twoPages), reportedPages: 1);

        Assert.Equal(2, extraction.Pages);
        Assert.Equal(1, extraction.ReportedPages);
    }

    [Fact]
    public void AnOutputThatIsNotWellFormedIsRefused()
    {
        Assert.Throws<OracleInstrumentException>(() => BboxText.Parse(Stream("<html><page width=\"1\" height=\"1\">"), reportedPages: 1));
    }

    [Fact]
    public void AnOutputWithoutAPageIsRefused()
    {
        Assert.Throws<OracleInstrumentException>(() => BboxText.Parse(Stream("<html><body></body></html>"), reportedPages: 1));
    }

    [Fact]
    public void ABoxWithoutEveryEdgeIsRefused()
    {
        var text = "<html><body><page width=\"595\" height=\"841\"><word xMin=\"1\" yMin=\"2\">a</word></page></body></html>";

        Assert.Throws<OracleInstrumentException>(() => BboxText.Parse(Stream(text), reportedPages: 1));
    }

    [Fact]
    public void APageCountIsReadFromTheFilesInformation()
    {
        var output = "Producer:        poppler\nTitle:           report\nPages:           36\nPage size:       595.276 x 841.89 pts\n";

        Assert.Equal(36, PopplerText.PageCount(output));
    }

    [Fact]
    public void AFileInformationWithoutAPageCountIsRefused()
    {
        Assert.Throws<OracleInstrumentException>(() => PopplerText.PageCount("Producer: poppler\n"));
    }

    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));
}
