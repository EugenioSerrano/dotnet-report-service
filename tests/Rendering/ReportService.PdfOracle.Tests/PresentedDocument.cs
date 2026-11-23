namespace ReportService.PdfOracle.Tests;

/// <summary>
/// A document whose date and amount are printed through a presentation function, and whose
/// close prints the job's acceptance instant: the snapshot is the one the plain synthetic
/// document was printed from, and every expectation of the printed text is computed from it and
/// from the culture and zone the declaration names.
/// </summary>
internal static class PresentedDocument
{
    /// <summary>How many pages the built document has.</summary>
    public const int Pages = 2;

    /// <summary>The width of an A4 page in points.</summary>
    public const double PageWidthPt = SyntheticDocument.PageWidthPt;

    /// <summary>The height of an A4 page in points.</summary>
    public const double PageHeightPt = SyntheticDocument.PageHeightPt;

    /// <summary>The snapshot the built document was printed from.</summary>
    public const string FixtureJson = SyntheticDocument.FixtureJson;

    /// <summary>The page, the columns and the presented values the package declares.</summary>
    public const string ExpectationJson = """
        {
          "paper": "A4",
          "widthMm": 210,
          "heightMm": 297,
          "marginMm": { "top": 15, "right": 12, "bottom": 18, "left": 12 },
          "columns": [
            { "column": "sequence", "startMm": 12, "endMm": 26 },
            { "column": "date", "startMm": 26, "endMm": 48 },
            { "column": "description", "startMm": 48, "endMm": 168 },
            { "column": "amount", "startMm": 168, "endMm": 198 }
          ],
          "rowKey": "sequence",
          "amountColumn": "amount",
          "headRow": ["Seq", "Date", "Description", "Amount"],
          "headerAnchors": ["$.reportTitle", "$.customer.name", "$.currency", "$.totals.recordCount"],
          "grouped": false,
          "blockTotal": false,
          "presented": {
            "culture": "es-AR",
            "timeZone": "America/Argentina/Buenos_Aires",
            "clock": "2026-01-01T00:00:00.0000000+00:00",
            "clockFormat": "dd/MM/yyyy HH:mm",
            "fields": [
              { "column": "date", "member": "date", "function": "format_date", "format": "dd/MM/yyyy" },
              { "column": "amount", "member": "amount", "function": "format_number", "format": "N2" }
            ]
          }
        }
        """;

    /// <summary>The data the built document was printed from.</summary>
    public static FixtureSource Fixture() => FixtureSource.Parse(FixtureJson, Expectation());

    /// <summary>The page and columns the package declares.</summary>
    public static ExpectationDocument Expectation() => ExpectationDocument.Parse(ExpectationJson);

    /// <summary>The words of a document that prints every value as the declaration says.</summary>
    public static IReadOnlyList<PdfWord> Words() =>
    [
        new(1, "Pilot", 40, 45, 60, 57),
        new(1, "report", 62, 45, 95, 57),
        new(1, "ACME", 40, 60, 70, 72),
        new(1, "·", 75, 60, 78, 72),
        new(1, "ARS", 82, 60, 100, 72),
        new(1, "·", 104, 60, 107, 72),
        new(1, "3", 110, 60, 116, 72),

        .. SyntheticDocument.HeadRow(1, 85),
        .. Row(1, 105, "1", "01/01/2026", ["alpha", "beta", "gamma"], "100,00"),
        .. Row(1, 125, "2", "02/01/2026", ["delta", "epsilon", "zeta", "eta"], "200,00"),

        .. SyntheticDocument.HeadRow(2, 45),
        new(2, "theta", 140, 75, 170, 87),
        new(2, "iota", 175, 75, 200, 87),
        .. Row(2, 95, "3", "03/01/2026", ["nu", "xi", "omicron"], "300,00"),

        // The document's close prints the job's acceptance instant in its culture and zone.
        new(2, "31/12/2025", 40, 320, 90, 332),
        new(2, "21:00", 95, 320, 120, 332),
    ];

    /// <summary>The built document, as the judge reads it.</summary>
    public static PdfExtraction Extraction() =>
        new(Pages, Pages, [(PageWidthPt, PageHeightPt), (PageWidthPt, PageHeightPt)], Words());

    /// <summary>One detail row: its number, its presented date, its description words and its presented amount.</summary>
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
}
