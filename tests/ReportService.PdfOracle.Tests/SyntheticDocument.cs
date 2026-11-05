namespace ReportService.PdfOracle.Tests;

/// <summary>
/// A printed document built by hand, with the data it was printed from, the page
/// its package declares, and the words of a well-printed report: a document header,
/// the column captions repeated on each page, three detail rows, and one row that
/// continues over the page break. Its geometry is the page the rendering suite's
/// packages declare, so a rule proved on it is a rule about those packages.
/// </summary>
internal static class SyntheticDocument
{
    /// <summary>How many pages the built document has.</summary>
    public const int Pages = 2;

    /// <summary>The width of an A4 page in points.</summary>
    public const double PageWidthPt = 210 * 72.0 / 25.4;

    /// <summary>The height of an A4 page in points.</summary>
    public const double PageHeightPt = 297 * 72.0 / 25.4;

    /// <summary>The snapshot the built document was printed from, as the judge reads it.</summary>
    public const string FixtureJson = """
        {
          "contractVersion": "1.0",
          "header": {
            "reportTitle": "Pilot report",
            "customer": { "name": "ACME" },
            "currency": "ARS",
            "totals": { "recordCount": 3 },
            "freeText": ""
          },
          "items": [
            { "sequence": 1, "date": "2026-01-01", "description": "alpha beta gamma", "amount": 100.00 },
            { "sequence": 2, "date": "2026-01-02", "description": "delta epsilon zeta eta theta iota", "amount": 200.00 },
            { "sequence": 3, "date": "2026-01-03", "description": "nu xi omicron", "amount": 300.00 }
          ]
        }
        """;

    /// <summary>The page and columns the package declares, as the judge reads them.</summary>
    public const string ExpectationJson = """
        {
          "paper": "A4",
          "widthMm": 210,
          "heightMm": 297,
          "marginMm": { "top": 15, "right": 12, "bottom": 18, "left": 12 },
          "columns": [
            { "column": "seq", "startMm": 12, "endMm": 26 },
            { "column": "date", "startMm": 26, "endMm": 48 },
            { "column": "description", "startMm": 48, "endMm": 168 },
            { "column": "amount", "startMm": 168, "endMm": 198 }
          ],
          "headRow": ["Seq", "Date", "Description", "Amount"],
          "headerAnchors": ["$.reportTitle", "$.customer.name", "$.currency", "$.totals.recordCount"],
          "trail": "$.freeText",
          "grouped": false,
          "blockTotal": false
        }
        """;

    /// <summary>The data the built document was printed from.</summary>
    public static FixtureSource Fixture() => FixtureSource.Parse(FixtureJson);

    /// <summary>The page and columns the package declares.</summary>
    public static ExpectationDocument Expectation() => ExpectationDocument.Parse(ExpectationJson);

    /// <summary>The words of a well-printed report.</summary>
    public static IReadOnlyList<PdfWord> Words() =>
    [
        // The document header: the title, then the customer line.
        new(1, "Pilot", 40, 45, 60, 57),
        new(1, "report", 62, 45, 95, 57),
        new(1, "ACME", 40, 60, 70, 72),
        new(1, "·", 75, 60, 78, 72),
        new(1, "ARS", 82, 60, 100, 72),
        new(1, "·", 104, 60, 107, 72),
        new(1, "3", 110, 60, 116, 72),

        // Page 1: the column captions, one whole row and the first page of a row that continues.
        .. HeadRow(1, 85),
        .. Row(1, 105, "1", "2026-01-01", ["alpha", "beta", "gamma"], "100.00"),
        .. Row(1, 125, "2", "2026-01-02", ["delta", "epsilon", "zeta", "eta"], "200.00"),

        // Page 2: the captions again, the end of the row, then the last row.
        .. HeadRow(2, 45),
        new(2, "theta", 140, 75, 170, 87),
        new(2, "iota", 175, 75, 200, 87),
        .. Row(2, 95, "3", "2026-01-03", ["nu", "xi", "omicron"], "300.00"),
    ];

    /// <summary>The built document, as the judge reads it.</summary>
    public static PdfExtraction Extraction() => new(Pages, Pages, [(PageWidthPt, PageHeightPt), (PageWidthPt, PageHeightPt)], Words());

    /// <summary>The four column captions of a page.</summary>
    public static IReadOnlyList<PdfWord> HeadRow(int page, double top) =>
    [
        new(page, "Seq", 40, top, 58, top + 12),
        new(page, "Date", 80, top, 104, top + 12),
        new(page, "Description", 140, top, 200, top + 12),
        new(page, "Amount", 500, top, 530, top + 12),
    ];

    /// <summary>One detail row: its number, its date, its description words and its amount.</summary>
    public static IReadOnlyList<PdfWord> Row(int page, double top, string sequence, string date, IReadOnlyList<string> description, string amount)
    {
        var words = new List<PdfWord>
        {
            new(page, sequence, 40, top, 46, top + 12),
            new(page, date, 80, top, 120, top + 12),
        };
        var left = 140.0;
        foreach (var word in description)
        {
            words.Add(new PdfWord(page, word, left, top, left + 20, top + 12));
            left += 25;
        }

        words.Add(new PdfWord(page, amount, 500, top, 525, top + 12));
        return words;
    }

    /// <summary>A word in the amount column of a page.</summary>
    public static PdfWord AmountWord(int page, double top, string text) => new(page, text, 500, top, 530, top + 12);
}
