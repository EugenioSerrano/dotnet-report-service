namespace ReportService.PdfOracle.Tests;

/// <summary>
/// A printed document built by hand as a page frame whose first page ends between two
/// detail rows: the header, the footer and the numbering in the page's bands, the column
/// headers on both pages, the first two rows whole on page 1 and the last two whole on
/// page 2. Nothing continues across the break, so the fill clause of the content frame is
/// not applied to it, however much of page 1 the reading leaves empty.
/// </summary>
internal static class BetweenRowsDocument
{
    /// <summary>How many pages the built document has.</summary>
    public const int Pages = 2;

    /// <summary>The built document, as the judge reads it.</summary>
    public static PdfExtraction Extraction() => new(
        Pages,
        Pages,
        [(PageFrameDocument.PageWidthPt, PageFrameDocument.PageHeightPt), (PageFrameDocument.PageWidthPt, PageFrameDocument.PageHeightPt)],
        Words(),
        [0, 0]);

    private static IReadOnlyList<PdfWord> Words() =>
    [
        .. PageFrameDocument.Bands(1, Pages),

        // The document header: the title, then the customer line.
        new(1, "Pilot", 40, 45, 60, 57),
        new(1, "report", 62, 45, 95, 57),
        new(1, "ACME", 40, 60, 70, 72),
        new(1, "C-1", 75, 60, 95, 72),
        new(1, "ARS", 100, 60, 130, 72),

        // Page 1: the captions and the first two rows, whole.
        .. PageFrameDocument.HeadRow(1, 85),
        .. PageFrameDocument.Row(1, 105, "1", "2026-01-01", ["alpha", "beta", "gamma"], "100.00"),
        .. PageFrameDocument.Row(1, 130, "2", "2026-01-02", ["delta", "epsilon", "zeta"], "200.00"),

        // Page 2: the captions, the last two rows and the text after the table.
        .. PageFrameDocument.Bands(2, Pages),
        .. PageFrameDocument.HeadRow(2, 45),
        .. PageFrameDocument.Row(2, 75, "3", "2026-01-03", ["etha", "iota", "kappa"], "300.00"),
        .. PageFrameDocument.Row(2, 100, "4", "2026-01-04", ["theta", "iota", "kappa", "lambda", "mu", "nu", "xi", "omicron", "pi"], "400.00"),
        new(2, "4", 140, 140, 150, 152),
    ];
}
