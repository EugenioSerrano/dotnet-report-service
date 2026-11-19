using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReportService.PdfOracle;

/// <summary>One failed rule: which rule, what kind of failure, and where it was seen.</summary>
/// <param name="Rule">The rule that failed.</param>
/// <param name="Class">The kind of failure: lost, duplicated, out of order, dropped, repeated or altered text, or lost structure.</param>
/// <param name="Row">The record number of the row, when the failure belongs to one.</param>
/// <param name="Field">The document member or anchor the failure belongs to, when it belongs to one.</param>
/// <param name="Page">The page the failure was seen on, when it was seen on one.</param>
/// <param name="Detail">What was seen, in counts and positions: never a value of the data or of the printed text.</param>
internal sealed record Failure(string Rule, string Class, int? Row = null, string? Field = null, int? Page = null, string? Detail = null);

/// <summary>The verdict on one printed document.</summary>
internal sealed class Verdict
{
    /// <summary>The file the verdict is about.</summary>
    public required string File { get; init; }

    /// <summary>How many pages the reading covers.</summary>
    public required int Pages { get; init; }

    /// <summary>How many words it holds.</summary>
    public required int Words { get; init; }

    /// <summary>The state of each rule: pass, fail, or not applicable to this package.</summary>
    public required IReadOnlyDictionary<string, string> Rules { get; init; }

    /// <summary>Why a rule does not apply to this package.</summary>
    public required IReadOnlyDictionary<string, string> NotApplicable { get; init; }

    /// <summary>Every failed rule.</summary>
    public required IReadOnlyList<Failure> Failures { get; init; }

    /// <summary>Whether the document passed every rule that applies to it.</summary>
    public bool Passed => Failures.Count == 0;

    /// <summary>The verdict as the JSON document another tool reads.</summary>
    public string ToJson()
    {
        var failures = new JsonArray();
        foreach (var failure in Failures)
        {
            failures.Add(new JsonObject
            {
                ["rule"] = failure.Rule,
                ["class"] = failure.Class,
                ["row"] = failure.Row,
                ["field"] = failure.Field,
                ["page"] = failure.Page,
                ["detail"] = failure.Detail,
            });
        }

        var notApplicable = new JsonObject();
        foreach (var (rule, reason) in NotApplicable)
        {
            notApplicable[rule] = reason;
        }

        var rules = new JsonObject();
        foreach (var (rule, state) in Rules)
        {
            rules[rule] = state;
        }

        var root = new JsonObject
        {
            ["file"] = File,
            ["pages"] = Pages,
            ["words"] = Words,
            ["passed"] = Passed,
            ["rules"] = rules,
            ["notApplicable"] = notApplicable,
            ["failures"] = failures,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>
/// The text-integrity rules the judge applies to a printed document: its structure,
/// every record printed once and in order, the text of each row against the record
/// it came from, the document header and the text after the table, the marks a page
/// may not print, and - for a document judged as a page frame - the page's geometry,
/// its page elements and numbering, its content frame and its repeated column
/// headers. Every expectation comes from the fixture the document was printed from
/// and from the declaration written beside the package, never from the composition
/// the document was laid out from.
/// </summary>
internal static class IntegrityRules
{
    /// <summary>The mark a page may not print on the layout this judge reads.</summary>
    public const string ContinuesMark = "(continues)";

    /// <summary>How far a printed page's size may be from the size the package declares, in points.</summary>
    public const double PageSizeTolerancePt = 0.5;

    /// <summary>How far a page frame's size may be from the declared one, in points.</summary>
    public const double FrameTolerancePt = 1.0;

    /// <summary>How far the column-header line's first and last word may be from the declared inset, in points.</summary>
    public const double HeadInsetTolerancePt = 1.5;

    /// <summary>The cell padding the page-frame package declares, in points.</summary>
    public const double HeadInsetPt = 4.0;

    /// <summary>How far above the content's top edge the first word of a continued page may sit, in points.</summary>
    public const double ContentTopOvershootPt = 1.0;

    /// <summary>How far below the content's top edge the first word of a continued page may begin, in points.</summary>
    public const double ContentTopSlackPt = 6.0;

    /// <summary>
    /// How much of its content height a page that ends inside a continued detail cell may
    /// leave unfilled, in points: the greatest gap measured over that population on the
    /// packages and geometries the reports are printed with (22.843 pt, on Letter
    /// landscape), plus two points. A page that ends between rows leaves up to one row step
    /// of the table by design and is not measured against it.
    /// </summary>
    public const double FillTolerancePt = 24.843;

    /// <summary>How far a page's lowest content word may cross the content's bottom edge, in points.</summary>
    public const double BottomOvershootPt = 1.0;

    /// <summary>Judges <paramref name="extraction"/> against <paramref name="fixture"/>.</summary>
    public static Verdict Judge(PdfExtraction extraction, FixtureSource fixture, ExpectationDocument expectation, string file)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(expectation);

        var placed = Placement.Place(extraction, expectation);
        var classified = Classification.Classify(placed, fixture, expectation);
        var failures = new List<Failure>();

        Structure(extraction, placed, classified, expectation, failures);
        HeadRows(classified, extraction, failures);
        Rows(classified, fixture, expectation, failures);
        Regions(classified, fixture, expectation, failures);
        Presented(classified, fixture, expectation, failures);
        Marks(placed, classified, failures);
        PageGeometry(extraction, expectation, failures);
        PageElements(placed, extraction, expectation, failures);
        ContentFrame(extraction, placed, classified, expectation, failures);
        RepeatedHeadRows(placed, extraction, expectation, failures);

        var rules = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (var number = 1; number <= 12; number++)
        {
            var rule = "TI-" + number.ToString(CultureInfo.InvariantCulture);
            rules[rule] = RuleState(rule, expectation, failures);
        }

        var notApplicable = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TI-5"] = "the packages this judge reads begin no group and print no block total",
        };
        if (!expectation.PageFrame)
        {
            const string reason = "the package declares no page frame; its page geometry, content frame and repeated headers are not judged here";
            notApplicable["TI-9"] = reason;
            notApplicable["TI-10"] = reason;
            notApplicable["TI-11"] = reason;
        }
        else if (!expectation.RepeatHeadRow)
        {
            notApplicable["TI-11"] = "the package declares no repeated column headers on this path";
        }

        if (expectation.Presented is not { Applies: true })
        {
            notApplicable["TI-12"] = "the package presents no value through a formatting function";
        }

        return new Verdict
        {
            File = file,
            Pages = extraction.Pages,
            Words = extraction.Words.Count,
            Rules = rules,
            NotApplicable = notApplicable,
            Failures = failures,
        };
    }

    private static string RuleState(string rule, ExpectationDocument expectation, IReadOnlyList<Failure> failures)
    {
        if (rule == "TI-5")
        {
            return "n/a";
        }

        if (!expectation.PageFrame && rule is "TI-9" or "TI-10" or "TI-11")
        {
            return "n/a";
        }

        if (expectation.PageFrame && rule == "TI-11" && !expectation.RepeatHeadRow)
        {
            return "n/a";
        }

        if (rule == "TI-12" && expectation.Presented is not { Applies: true })
        {
            return "n/a";
        }

        return failures.Any(failure => string.Equals(failure.Rule, rule, StringComparison.Ordinal)) ? "fail" : "pass";
    }

    /// <summary>The page's frame, its edges and the two bands no text may print in.</summary>
    private static void Structure(
        PdfExtraction extraction,
        PlacedDocument placed,
        ClassifiedDocument classified,
        ExpectationDocument expectation,
        List<Failure> failures)
    {
        if (extraction.Pages != extraction.ReportedPages)
        {
            failures.Add(new Failure("TI-1", "structure-lost", Detail: $"the text covers {Count(extraction.Pages)} page(s), the file reports {Count(extraction.ReportedPages)}"));
        }

        if (!expectation.PageFrame)
        {
            for (var index = 0; index < extraction.PageSizes.Count; index++)
            {
                var (width, height) = extraction.PageSizes[index];
                if (Math.Abs(width - expectation.WidthPt) > PageSizeTolerancePt || Math.Abs(height - expectation.HeightPt) > PageSizeTolerancePt)
                {
                    failures.Add(new Failure("TI-1", "structure-lost", Page: index + 1, Detail: "the page is not the size the package declares"));
                }
            }
        }

        // A page frame's own tolerance for a word at its edge is TI-10's; the packages
        // without one keep the strict rule.
        if (!expectation.PageFrame)
        {
            foreach (var page in placed.Crossing.GroupBy(word => word.Page))
            {
                failures.Add(new Failure("TI-1", "structure-lost", Page: page.Key, Detail: $"{Count(page.Count())} word(s) cross the content area's edge"));
            }
        }

        // A package that declares page elements prints them in the two bands; their
        // words are read by TI-8. A band no element is declared for prints nothing there.
        if (expectation.PageHeader is null && expectation.PageNumbering is null)
        {
            for (var index = 0; index < placed.HeaderBand.Count; index++)
            {
                if (placed.HeaderBand[index].Count > 0)
                {
                    failures.Add(new Failure("TI-1", "structure-lost", Page: index + 1, Detail: $"the header band holds {Count(placed.HeaderBand[index].Count)} word(s)"));
                }
            }
        }

        if (expectation.PageFooter is null)
        {
            for (var index = 0; index < placed.FooterBand.Count; index++)
            {
                if (placed.FooterBand[index].Count > 0)
                {
                    failures.Add(new Failure("TI-1", "structure-lost", Page: index + 1, Detail: $"the footer band holds {Count(placed.FooterBand[index].Count)} word(s)"));
                }
            }
        }

        var wordsByPage = new int[extraction.Pages];
        foreach (var word in extraction.Words)
        {
            if (word.Page >= 1 && word.Page <= extraction.Pages)
            {
                wordsByPage[word.Page - 1]++;
            }
        }

        for (var page = 1; page <= extraction.Pages; page++)
        {
            if (wordsByPage[page - 1] == 0)
            {
                failures.Add(new Failure("TI-1", "structure-lost", Page: page, Detail: "the page holds no text"));
            }
        }

        foreach (var line in classified.Unclassified)
        {
            failures.Add(new Failure("TI-1", "structure-lost", Page: line.Page, Detail: "a content line belongs to no region"));
        }
    }

    /// <summary>
    /// The column captions print on every page that prints a detail row. A page that
    /// holds only the document header or the text after the table prints no column,
    /// so it needs no caption: repeating a head row is the page-elements work, not
    /// this rule.
    /// </summary>
    private static void HeadRows(ClassifiedDocument classified, PdfExtraction extraction, List<Failure> failures)
    {
        if (classified.HeadRowPages.Count == 0)
        {
            failures.Add(new Failure("TI-8", "structure-lost", Detail: "no column captions line"));
            return;
        }

        var pages = classified.HeadRowPages.ToHashSet();
        var rowPages = classified.Rows.SelectMany(row => row.Pages).ToHashSet();
        for (var page = 1; page <= extraction.Pages; page++)
        {
            if (rowPages.Contains(page) && !pages.Contains(page))
            {
                failures.Add(new Failure("TI-8", "structure-lost", Page: page, Detail: "the page prints no column captions"));
            }
        }
    }

    /// <summary>Every record once, in order, with every declared column's own text.</summary>
    private static void Rows(ClassifiedDocument classified, FixtureSource fixture, ExpectationDocument expectation, List<Failure> failures)
    {
        var expected = fixture.Records.Select(record => record.Key).ToList();
        var actual = classified.Rows.Select(row => row.Key).ToList();
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var printed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in actual)
        {
            printed[key] = printed.GetValueOrDefault(key) + 1;
        }

        var coverage = false;
        foreach (var key in expected)
        {
            if (!printed.ContainsKey(key))
            {
                failures.Add(new Failure("TI-2", "row-lost", Row: NumberOf(key)));
                coverage = true;
            }
        }

        foreach (var key in actual.Where(key => printed[key] > 1).Distinct(StringComparer.Ordinal))
        {
            var second = classified.Rows.Where(row => string.Equals(row.Key, key, StringComparison.Ordinal)).Skip(1).First();
            failures.Add(new Failure("TI-2", "row-duplicated", Row: NumberOf(key), Page: second.AnchorPage, Detail: $"{Count(printed[key])} rows print it"));
            coverage = true;
        }

        foreach (var row in classified.Rows.Where(row => !expectedSet.Contains(row.Key)))
        {
            failures.Add(new Failure("TI-2", "row-order", Row: row.Number, Page: row.AnchorPage, Detail: "the record is not in the fixture"));
            coverage = true;
        }

        if (!coverage && !expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            var index = Enumerable.Range(0, Math.Min(expected.Count, actual.Count))
                .First(position => !string.Equals(expected[position], actual[position], StringComparison.Ordinal));
            failures.Add(new Failure("TI-2", "row-order", Row: classified.Rows[index].Number, Page: classified.Rows[index].AnchorPage, Detail: $"position {Count(index + 1)} of the detail"));
        }

        var byKey = new Dictionary<string, ItemRow>(StringComparer.Ordinal);
        foreach (var row in classified.Rows)
        {
            byKey.TryAdd(row.Key, row);
        }

        foreach (var record in fixture.Records)
        {
            if (!byKey.TryGetValue(record.Key, out var row))
            {
                continue;
            }

            // Every declared column is compared with the record's member of the same name,
            // and the column the declaration names as the amount carries the rule that the
            // value prints on the page the row begins on. A column the declaration lists as
            // presented is held to the text its declared culture and zone produce (TI-12),
            // never to the raw member: a formatted value is not the value it was computed from.
            foreach (var column in expectation.Columns)
            {
                var words = Field(row, column.Column);
                var presented = IsPresented(expectation, column.Column);
                if (string.Equals(column.Column, expectation.AmountColumn, StringComparison.Ordinal))
                {
                    if (!presented)
                    {
                        CheckField("TI-4", row.Number, column.Column, words, record.Member(column.Column), failures);
                    }

                    CheckAmountOnItsRowsFirstPage(row, column.Column, words, failures);
                }
                else if (!presented)
                {
                    CheckField("TI-3", row.Number, column.Column, words, record.Member(column.Column), failures);
                }
            }
        }
    }

    /// <summary>The document header, and the text the package prints after the table.</summary>
    private static void Regions(ClassifiedDocument classified, FixtureSource fixture, ExpectationDocument expectation, List<Failure> failures)
    {
        var lead = Normalisation.N(string.Join(' ', classified.Lead.Select(line => line.Text)));
        var position = 0;
        foreach (var anchor in expectation.HeaderAnchors)
        {
            var value = Normalisation.N(fixture.ValueOf(anchor));
            if (value.Length == 0)
            {
                continue;
            }

            var found = lead.IndexOf(value, position, StringComparison.Ordinal);
            if (found < 0)
            {
                var anywhere = lead.Contains(value, StringComparison.Ordinal);
                failures.Add(new Failure("TI-6", anywhere ? "text-altered" : "text-dropped", Field: anchor));
                continue;
            }

            position = found + value.Length;
        }

        if (expectation.Trail is null)
        {
            return;
        }

        var expected = Normalisation.N(fixture.ValueOf(expectation.Trail));
        var words = classified.Trail.SelectMany(line => line.Words).ToList();
        var printed = Normalisation.N(string.Join(' ', words.Select(word => word.Text)));
        if (expected.Length == 0)
        {
            if (printed.Length > 0)
            {
                failures.Add(new Failure("TI-6", "structure-lost", Field: expectation.Trail, Detail: "text prints after the table that the data does not hold"));
            }

            return;
        }

        var difference = Normalisation.Compare(expected, printed);
        if (difference is not null)
        {
            failures.Add(new Failure("TI-6", difference.Class, Field: expectation.Trail, Page: PageAt(words, difference.Offset)));
        }
    }

    /// <summary>
    /// The values the package presents through a formatting function: every declared field is
    /// computed from the record and the declaration's culture and zone, and the printed text of
    /// the row's column must equal it; a value printed in another culture, another zone or
    /// another rounding is a wrong document even when the composition printed exactly what it
    /// composed. The acceptance instant the declaration names is held to its expected text on
    /// every page its line prints on.
    /// </summary>
    private static void Presented(
        ClassifiedDocument classified,
        FixtureSource fixture,
        ExpectationDocument expectation,
        List<Failure> failures)
    {
        if (expectation.Presented is not { Applies: true } presented)
        {
            return;
        }

        var byKey = new Dictionary<string, ItemRow>(StringComparer.Ordinal);
        foreach (var row in classified.Rows)
        {
            byKey.TryAdd(row.Key, row);
        }

        foreach (var record in fixture.Records)
        {
            if (!byKey.TryGetValue(record.Key, out var row))
            {
                continue;
            }

            foreach (var field in presented.Fields)
            {
                var expected = presented.Expected(record, field);
                if (expected is null)
                {
                    failures.Add(new Failure(
                        "TI-12",
                        "presented-value-wrong",
                        Row: row.Number,
                        Field: field.Column,
                        Page: row.AnchorPage,
                        Detail: "the value is not one the presentation rule reads"));
                    continue;
                }

                var printed = Normalisation.N(string.Join(' ', Field(row, field.Column).Select(word => word.Text)));
                if (!string.Equals(Normalisation.N(expected), printed, StringComparison.Ordinal))
                {
                    failures.Add(new Failure(
                        "TI-12",
                        "presented-value-wrong",
                        Row: row.Number,
                        Field: field.Column,
                        Page: row.AnchorPage,
                        Detail: "the printed text is not the expected presentation"));
                }
            }
        }

        if (presented.ExpectedClock() is not { } clock)
        {
            return;
        }

        var lines = classified.Trail;
        var text = Normalisation.N(string.Join(' ', lines.Select(line => line.Text)));
        if (!string.Equals(Normalisation.N(clock), text, StringComparison.Ordinal))
        {
            failures.Add(new Failure(
                "TI-12",
                "presented-value-wrong",
                Field: "render.clock",
                Page: lines.Count > 0 ? lines[0].Page : null,
                Detail: "the printed text is not the expected presentation"));
        }
    }

    private static bool IsPresented(ExpectationDocument expectation, string column) =>
        expectation.Presented?.Fields.Any(field => string.Equals(field.Column, column, StringComparison.Ordinal)) == true;

    /// <summary>The mark the layout must not print, inside a row or anywhere else.</summary>
    private static void Marks(PlacedDocument placed, ClassifiedDocument classified, List<Failure> failures)
    {
        foreach (var row in classified.Rows)
        {
            foreach (var (column, word) in row.Words.Where(entry => entry.Word.Text.Contains(ContinuesMark, StringComparison.Ordinal)))
            {
                failures.Add(new Failure("TI-7", "text-altered", Row: row.Number, Field: column, Page: word.Page, Detail: "a continuation mark prints"));
            }
        }

        var outside = placed.HeaderBand.Concat(placed.FooterBand)
            .SelectMany(band => band)
            .Concat(classified.Lead.SelectMany(line => line.Words))
            .Concat(classified.Trail.SelectMany(line => line.Words))
            .Concat(classified.Unclassified.SelectMany(line => line.Words))
            .Where(word => word.Text.Contains(ContinuesMark, StringComparison.Ordinal));
        foreach (var page in outside.GroupBy(word => word.Page))
        {
            failures.Add(new Failure("TI-7", "structure-lost", Page: page.Key, Detail: "a continuation mark prints outside every row"));
        }
    }

    /// <summary>
    /// The page's geometry: every page is the declared size within one point, the page
    /// is not rotated, and every page is the same size as the first.
    /// </summary>
    private static void PageGeometry(PdfExtraction extraction, ExpectationDocument expectation, List<Failure> failures)
    {
        if (!expectation.PageFrame)
        {
            return;
        }

        for (var index = 0; index < extraction.PageSizes.Count; index++)
        {
            var (width, height) = extraction.PageSizes[index];
            if (Math.Abs(width - expectation.WidthPt) > FrameTolerancePt || Math.Abs(height - expectation.HeightPt) > FrameTolerancePt)
            {
                failures.Add(new Failure(
                    "TI-9",
                    "page-geometry-wrong",
                    Page: index + 1,
                    Detail: $"{ExpectationDocument.Points.Format(width)} x {ExpectationDocument.Points.Format(height)} pt, not the declared "
                        + $"{ExpectationDocument.Points.Format(expectation.WidthPt)} x {ExpectationDocument.Points.Format(expectation.HeightPt)} pt"));
            }
        }

        if (extraction.Rotations.Count == extraction.Pages)
        {
            for (var index = 0; index < extraction.Rotations.Count; index++)
            {
                if (extraction.Rotations[index] != 0)
                {
                    failures.Add(new Failure(
                        "TI-9",
                        "page-geometry-wrong",
                        Page: index + 1,
                        Detail: $"the page is rotated {Count(extraction.Rotations[index])} degrees"));
                }
            }
        }
    }

    /// <summary>
    /// The page's repeated elements: the header band reads the header wording and the
    /// page's own position and the document's page count; the footer band reads the
    /// footer wording. Every page is judged; a missing element is a failure.
    /// </summary>
    private static void PageElements(
        PlacedDocument placed,
        PdfExtraction extraction,
        ExpectationDocument expectation,
        List<Failure> failures)
    {
        if (!expectation.HasPageElements)
        {
            return;
        }

        for (var page = 1; page <= extraction.Pages; page++)
        {
            var expectedHeader = Combine(
                expectation.PageHeader,
                expectation.PageNumbering is { } wording ? NumberingOf(wording, page, extraction.Pages) : null);
            var expectedFooter = expectation.PageFooter;
            if (expectedHeader is not null && !Reads(placed.HeaderBand[page - 1], expectedHeader))
            {
                var headerWords = BandText(placed.HeaderBand[page - 1]);
                var headerPresent = expectation.PageHeader is null
                    || headerWords.StartsWith(Normalisation.N(expectation.PageHeader), StringComparison.Ordinal);
                failures.Add(new Failure(
                    "TI-8",
                    headerPresent ? "numbering-wrong" : "header-wrong",
                    Page: page,
                    Detail: headerPresent ? "the numbering reads other words or numbers" : "the header band reads other words"));
            }

            if (expectedFooter is not null && !Reads(placed.FooterBand[page - 1], expectedFooter))
            {
                failures.Add(new Failure("TI-8", "footer-wrong", Page: page, Detail: "the footer band reads other words"));
            }
        }
    }

    /// <summary>
    /// The content frame: every content word inside the declared rectangle; the column
    /// header line inset by the cell padding; a continued page starting at the content's
    /// top; and a page that is not the last filled down to the content's bottom.
    /// </summary>
    private static void ContentFrame(
        PdfExtraction extraction,
        PlacedDocument placed,
        ClassifiedDocument classified,
        ExpectationDocument expectation,
        List<Failure> failures)
    {
        if (!expectation.PageFrame)
        {
            return;
        }

        var left = ExpectationDocument.Points.Of(expectation.LeftMm);
        var right = ExpectationDocument.Points.Of(expectation.WidthMm - expectation.RightMm);
        var top = expectation.ContentTopPt;
        var bottom = expectation.ContentBottomPt;

        foreach (var word in extraction.Words)
        {
            if (word.YCentre < top || word.YCentre > bottom)
            {
                continue;
            }

            if (word.XMin < left - FrameTolerancePt
                || word.XMax > right + FrameTolerancePt
                || word.YMin < top - FrameTolerancePt
                || word.YMax > bottom + FrameTolerancePt)
            {
                failures.Add(new Failure("TI-10", "frame-wrong", Page: word.Page, Detail: "a content word lies outside the declared content rectangle"));
            }
        }

        var headRow = Normalisation.N(string.Join(' ', expectation.HeadRow));
        var headLine = placed.Lines.FirstOrDefault(line => Normalisation.N(line.Text) == headRow);
        if (headLine is not null && headLine.Words.Count > 0)
        {
            var first = headLine.Words.OrderBy(word => word.XMin).First();
            var last = headLine.Words.OrderBy(word => word.XMax).Last();
            if (Math.Abs(first.XMin - (left + HeadInsetPt)) > HeadInsetTolerancePt)
            {
                failures.Add(new Failure("TI-10", "frame-wrong", Page: first.Page, Detail: "the column header line does not start at the declared inset"));
            }

            if (Math.Abs(last.XMax - (right - HeadInsetPt)) > HeadInsetTolerancePt)
            {
                failures.Add(new Failure("TI-10", "frame-wrong", Page: last.Page, Detail: "the column header line does not end at the declared inset"));
            }
        }

        for (var page = 2; page <= extraction.Pages; page++)
        {
            var words = placed.Lines.Where(line => line.Page == page).SelectMany(line => line.Words).ToList();
            if (words.Count == 0)
            {
                continue;
            }

            var firstTop = words.Min(word => word.YMin);
            if (firstTop < top - ContentTopOvershootPt || firstTop > top + ContentTopSlackPt)
            {
                failures.Add(new Failure("TI-10", "frame-wrong", Page: page, Detail: "the page's content does not start at the declared top margin"));
            }
        }

        var endedInsideACell = PagesThatEndInsideAContinuedCell(classified);
        for (var page = 1; page < extraction.Pages; page++)
        {
            var words = placed.Lines.Where(line => line.Page == page).SelectMany(line => line.Words).ToList();
            if (words.Count == 0)
            {
                continue;
            }

            var lowest = words.Max(word => word.YMax);
            if (lowest > bottom + BottomOvershootPt)
            {
                failures.Add(new Failure("TI-10", "frame-wrong", Page: page, Detail: "the page's content runs past the declared bottom margin"));
            }
            else if (endedInsideACell.Contains(page) && bottom - lowest > FillTolerancePt)
            {
                failures.Add(new Failure("TI-10", "frame-wrong", Page: page, Detail: "the page ends well above the declared bottom margin"));
            }
        }
    }

    /// <summary>
    /// The pages that end inside a detail row the reading continues onto the page after
    /// them: the row's words printed on both pages. Only there must the layout fill the
    /// page, so only there is the fill of the content column measured; a page that ends
    /// between rows leaves up to one row step of the table by design.
    /// </summary>
    private static HashSet<int> PagesThatEndInsideAContinuedCell(ClassifiedDocument classified)
    {
        var pages = new HashSet<int>();
        foreach (var row in classified.Rows)
        {
            foreach (var page in row.Pages)
            {
                if (row.Pages.Contains(page + 1))
                {
                    pages.Add(page);
                }
            }
        }

        return pages;
    }

    /// <summary>
    /// The column headers on a continued page: the page's first content line is the
    /// column-header line, in the order the package prints it.
    /// </summary>
    private static void RepeatedHeadRows(
        PlacedDocument placed,
        PdfExtraction extraction,
        ExpectationDocument expectation,
        List<Failure> failures)
    {
        if (!expectation.PageFrame || !expectation.RepeatHeadRow)
        {
            return;
        }

        var headRow = Normalisation.N(string.Join(' ', expectation.HeadRow));
        for (var page = 2; page <= extraction.Pages; page++)
        {
            var lines = placed.Lines.Where(line => line.Page == page).ToList();
            if (lines.Count == 0)
            {
                continue;
            }

            if (!string.Equals(Normalisation.N(lines[0].Text), headRow, StringComparison.Ordinal))
            {
                failures.Add(new Failure("TI-11", "column-header-missing", Page: page, Detail: "the page does not begin with the column headers"));
            }
        }
    }

    /// <summary>The words of a page band, top to bottom and left to right, as one text.</summary>
    private static string BandText(IReadOnlyList<PdfWord> words) =>
        Normalisation.N(string.Join(
            ' ',
            words.OrderBy(word => word.YCentre).ThenBy(word => word.XMin).Select(word => word.Text)));

    /// <summary>Whether a page band reads exactly <paramref name="expected"/>.</summary>
    private static bool Reads(IReadOnlyList<PdfWord> words, string expected) =>
        string.Equals(BandText(words), Normalisation.N(expected), StringComparison.Ordinal);

    /// <summary>The two page elements that share the header band, as one text.</summary>
    private static string? Combine(string? left, string? right) =>
        left is null ? right : right is null ? left : left + " " + right;

    /// <summary>The numbering wording with the page's own numbers.</summary>
    private static string NumberingOf(string wording, int position, int pages) =>
        wording
            .Replace("{X}", position.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{Y}", pages.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static void CheckField(string rule, int? sequence, string field, IReadOnlyList<PdfWord> words, string expected, List<Failure> failures)
    {
        if (words.Any(word => Normalisation.HasUnmappedGlyph(word.Text)))
        {
            failures.Add(new Failure(rule, "text-altered", Row: sequence, Field: field, Page: words.First(word => Normalisation.HasUnmappedGlyph(word.Text)).Page, Detail: "a glyph could not be mapped"));
        }

        var difference = Normalisation.Compare(Normalisation.N(expected), Normalisation.N(string.Join(' ', words.Select(word => word.Text))));
        if (difference is not null)
        {
            failures.Add(new Failure(rule, difference.Class, Row: sequence, Field: field, Page: PageAt(words, difference.Offset)));
        }
    }

    /// <summary>The words of a row that printed in a column, the continuation mark excluded.</summary>
    private static IReadOnlyList<PdfWord> Field(ItemRow row, string column) =>
        [.. row.Words.Where(entry => entry.Column == column && !entry.Word.Text.Contains(ContinuesMark, StringComparison.Ordinal)).Select(entry => entry.Word)];

    /// <summary>
    /// The amount of a row belongs on the page the row begins on: a row that runs over
    /// a page break keeps its amount at its start, never on a page it continues on.
    /// The first page is taken from the row's own first text — the page its number
    /// printed on — so the declaration the document is read against names no page.
    /// </summary>
    private static void CheckAmountOnItsRowsFirstPage(ItemRow row, string column, IReadOnlyList<PdfWord> words, List<Failure> failures)
    {
        var elsewhere = words.FirstOrDefault(word => word.Page != row.AnchorPage);
        if (elsewhere is not null)
        {
            failures.Add(new Failure(
                "TI-4",
                "structure-lost",
                Row: row.Number,
                Field: column,
                Page: elsewhere.Page,
                Detail: $"the {column} prints on page {Count(elsewhere.Page)}; the row begins on page {Count(row.AnchorPage)}"));
        }
    }

    /// <summary>The row key as a record number, when it is one: a failure names the record it belongs to.</summary>
    private static int? NumberOf(string key) =>
        int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    /// <summary>The page of the word holding the normalised offset.</summary>
    private static int? PageAt(IReadOnlyList<PdfWord> words, int offset)
    {
        var position = 0;
        foreach (var word in words)
        {
            position += Normalisation.N(word.Text).Length;
            if (position > offset)
            {
                return word.Page;
            }
        }

        return words.Count > 0 ? words[^1].Page : null;
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
