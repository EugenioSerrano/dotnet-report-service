namespace ReportService.PdfOracle.Tests;

/// <summary>
/// A printed document built by hand as a page frame: three pages of an A4 portrait
/// report with the header, the footer and the numbering in the page's bands, the
/// column headers on every page, and the content filled down to the bottom margin on
/// every page but the last. Its geometry is the page the rendering suite's
/// page-frame package declares, so a rule proved on it is a rule about that package.
/// </summary>
internal static class PageFrameDocument
{
    /// <summary>How many pages the built document has.</summary>
    public const int Pages = 3;

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
            "customer": { "name": "ACME", "id": "C-1" },
            "currency": "ARS",
            "totals": { "recordCount": 4 },
            "freeText": ""
          },
          "items": [
            { "sequence": 1, "date": "2026-01-01", "description": "alpha beta gamma", "amount": 100.00 },
            { "sequence": 2, "date": "2026-01-02", "description": "delta epsilon zeta", "amount": 200.00 },
            { "sequence": 3, "date": "2026-01-03", "description": "etha iota kappa", "amount": 300.00 },
            { "sequence": 4, "date": "2026-01-04", "description": "theta iota kappa lambda mu nu xi omicron pi", "amount": 400.00 }
          ]
        }
        """;

    /// <summary>The page, the columns and the page elements the package declares.</summary>
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
          "headRow": ["Secuencia", "Fecha", "Descripción", "Importe"],
          "headerAnchors": ["$.reportTitle", "$.customer.name", "$.customer.id", "$.currency"],
          "trail": "$.totals.recordCount",
          "grouped": false,
          "blockTotal": false,
          "pageFrame": true,
          "pageHeader": "Estado de cuenta — prueba de marco",
          "pageFooter": "Documento interno · uso de prueba",
          "pageNumbering": "Hoja {X} de {Y}",
          "repeatHeadRow": true
        }
        """;

    /// <summary>The data the built document was printed from.</summary>
    public static FixtureSource Fixture() => FixtureSource.Parse(FixtureJson, Expectation());

    /// <summary>The page, the columns and the page elements the package declares.</summary>
    public static ExpectationDocument Expectation() => ExpectationDocument.Parse(ExpectationJson);

    /// <summary>The words of a well-printed report.</summary>
    public static IReadOnlyList<PdfWord> Words() =>
    [
        .. Bands(1, 3),
        // The document header: the title, then the customer line.
        new(1, "Pilot", 40, 45, 60, 57),
        new(1, "report", 62, 45, 95, 57),
        new(1, "ACME", 40, 60, 70, 72),
        new(1, "C-1", 75, 60, 95, 72),
        new(1, "ARS", 100, 60, 130, 72),

        // Page 1: the captions, three whole rows and the first page of the tall row.
        .. HeadRow(1, 85),
        .. Row(1, 105, "1", "2026-01-01", ["alpha", "beta", "gamma"], "100.00"),
        .. Row(1, 130, "2", "2026-01-02", ["delta", "epsilon", "zeta"], "200.00"),
        .. Row(1, 155, "3", "2026-01-03", ["etha", "iota", "kappa"], "300.00"),
        .. TallRow(1, 770, "4", "2026-01-04", ["theta", "iota", "kappa"], "400.00"),

        // Page 2: the captions, the tall row's continuation, filled to the bottom margin.
        .. Bands(2, 3),
        .. HeadRow(2, 45),
        new(2, "lambda", 140, 75, 170, 87),
        new(2, "mu", 175, 75, 195, 87),
        new(2, "nu", 140, 770, 155, 782),
        new(2, "xi", 160, 770, 175, 782),

        // Page 3: the captions, the rest of the tall row and the text after the table.
        .. Bands(3, 3),
        .. HeadRow(3, 45),
        new(3, "omicron", 140, 75, 180, 87),
        new(3, "pi", 185, 75, 195, 87),
        new(3, "4", 140, 100, 150, 112),
    ];

    /// <summary>The built document, as the judge reads it.</summary>
    public static PdfExtraction Extraction() => new(
        Pages,
        Pages,
        [(PageWidthPt, PageHeightPt), (PageWidthPt, PageHeightPt), (PageWidthPt, PageHeightPt)],
        Words(),
        [0, 0, 0]);

    /// <summary>The header, footer and numbering bands of one page.</summary>
    public static IReadOnlyList<PdfWord> Bands(int page, int pages)
    {
        double top = 20;
        return
        [
            new(page, "Estado", 40, top, 70, top + 10),
            new(page, "de", 75, top, 87, top + 10),
            new(page, "cuenta", 90, top, 125, top + 10),
            new(page, "—", 130, top, 140, top + 10),
            new(page, "prueba", 145, top, 175, top + 10),
            new(page, "de", 180, top, 192, top + 10),
            new(page, "marco", 195, top, 225, top + 10),
            new(page, "Hoja", 400, top, 425, top + 10),
            new(page, page.ToString(System.Globalization.CultureInfo.InvariantCulture), 428, top, 434, top + 10),
            new(page, "de", 438, top, 450, top + 10),
            new(page, pages.ToString(System.Globalization.CultureInfo.InvariantCulture), 453, top, 459, top + 10),
            new(page, "Documento", 40, 800, 90, 810),
            new(page, "interno", 95, 800, 135, 810),
            new(page, "·", 140, 800, 143, 810),
            new(page, "uso", 146, 800, 165, 810),
            new(page, "de", 168, 800, 180, 810),
            new(page, "prueba", 183, 800, 220, 810),
        ];
    }

    /// <summary>The four column captions of a page, inset by the declared cell padding.</summary>
    public static IReadOnlyList<PdfWord> HeadRow(int page, double top) =>
    [
        new(page, "Secuencia", 38.02, top, 90, top + 11),
        new(page, "Fecha", 120, top, 150, top + 11),
        new(page, "Descripción", 200, top, 280, top + 11),
        new(page, "Importe", 500, top, 557.26, top + 11),
    ];

    /// <summary>One detail row: its number, its date, its description words and its amount.</summary>
    public static IReadOnlyList<PdfWord> Row(
        int page,
        double top,
        string sequence,
        string date,
        IReadOnlyList<string> description,
        string amount)
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

    /// <summary>The first page of a row that continues on the pages after it.</summary>
    public static IReadOnlyList<PdfWord> TallRow(
        int page,
        double top,
        string sequence,
        string date,
        IReadOnlyList<string> description,
        string amount) => Row(page, top, sequence, date, description, amount);
}
