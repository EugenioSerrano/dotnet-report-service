using System.Globalization;

namespace ReportService.PdfOracle;

/// <summary>What one extraction-level control did and whether the judge saw it.</summary>
/// <param name="Id">The control's name.</param>
/// <param name="Mutation">What was changed in the extracted words.</param>
/// <param name="Expected">The failure the judge must report.</param>
/// <param name="Behaved">Whether it reported it.</param>
/// <param name="Observed">Every failure the judge reported, as rule, kind and place.</param>
internal sealed record ControlOutcome(string Id, string Mutation, string Expected, bool Behaved, IReadOnlyList<string> Observed);

/// <summary>
/// The extraction-level controls: changes made in memory to the words of one
/// document that passes, placed where printing fails most often — a word lost on the
/// page a row continues on, a repeated line, a lost row, altered text, a row out of
/// order, a duplicated row, a page without text, a record number the data does not
/// hold. Each control must be reported with the failure it plants; a control the
/// judge misses, or one it reports as something else, means the judge is not trusted
/// on that failure.
/// </summary>
internal static class ExtractionControls
{
    /// <summary>Runs every control over <paramref name="passing"/>.</summary>
    public static IReadOnlyList<ControlOutcome> Run(PdfExtraction passing, FixtureSource fixture, ExpectationDocument expectation)
    {
        ArgumentNullException.ThrowIfNull(passing);
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(expectation);

        var baseVerdict = IntegrityRules.Judge(passing, fixture, expectation, "base");
        if (!baseVerdict.Passed)
        {
            return [new ControlOutcome("base", "the document the controls act on", "pass", false, Described(baseVerdict))];
        }

        var placed = Placement.Place(passing, expectation);
        var classified = Classification.Classify(placed, fixture, expectation);
        if (classified.Rows.Count == 0)
        {
            return [new ControlOutcome("base", "a document with a detail row", "a row to act on", false, Described(baseVerdict))];
        }

        var controls = new List<ControlOutcome>
        {
            DeleteADescriptionWord(passing, fixture, expectation, classified),
            RepeatALine(passing, fixture, expectation, placed, classified),
            DeleteARow(passing, fixture, expectation, classified),
            SwapTwoWords(passing, fixture, expectation, classified),
            MoveARowOutOfOrder(passing, fixture, expectation, classified),
            DuplicateARow(passing, fixture, expectation, classified),
            BlankAPage(passing, fixture, expectation),
            PrintARecordTheDataDoesNotHold(passing, fixture, expectation, placed),
        };
        if (expectation.PageFrame)
        {
            controls.AddRange(PageFrameControls(passing, fixture, expectation, placed));
        }

        if (expectation.Presented is { } presented)
        {
            if (presented.NumericFields.Any())
            {
                controls.Add(AlterADecimalSeparator(passing, fixture, expectation, classified));
            }

            if (presented.Clock is not null)
            {
                controls.Add(ShiftTheClockByOneHour(passing, fixture, expectation, classified));
            }
        }

        return controls;
    }

    /// <summary>
    /// A presented number's decimal separator changed for the one of another culture: the
    /// composition prints what it composed, so only a reading that computes the text from the
    /// snapshot and the declared culture can refuse it.
    /// </summary>
    private static ControlOutcome AlterADecimalSeparator(
        PdfExtraction passing,
        FixtureSource fixture,
        ExpectationDocument expectation,
        ClassifiedDocument classified)
    {
        var field = expectation.Presented!.NumericFields.First();
        var row = Tallest(classified);
        var word = row.Words
            .Where(entry => string.Equals(entry.Column, field.Column, StringComparison.Ordinal))
            .Select(entry => entry.Word)
            .FirstOrDefault(candidate => candidate.Text.Contains(',', StringComparison.Ordinal) || candidate.Text.Contains('.', StringComparison.Ordinal));
        if (word is null)
        {
            return NotEvaluable("E15", "alter a presented number's decimal separator", "presented-value-wrong");
        }

        var altered = word.Text.Contains(',', StringComparison.Ordinal)
            ? ReplaceLast(word.Text, ',', '.')
            : ReplaceLast(word.Text, '.', ',');

        return ReplaceWordText("E15", $"alter the decimal separator of row {row.Key}", "presented-value-wrong", passing, fixture, expectation, word, altered);
    }

    /// <summary>An hour of the printed acceptance instant shifted by one: the zone and the clock are part of the declaration, and TI-12 holds the text to them.</summary>
    private static ControlOutcome ShiftTheClockByOneHour(
        PdfExtraction passing,
        FixtureSource fixture,
        ExpectationDocument expectation,
        ClassifiedDocument classified)
    {
        foreach (var line in classified.Trail)
        {
            foreach (var word in line.Words)
            {
                var parts = word.Text.Split(':');
                if (parts.Length != 2
                    || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
                    || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                var shifted = ((hour + 1) % 24).ToString("D2", CultureInfo.InvariantCulture) + ":" + parts[1];
                return ReplaceWordText("E16", "print the acceptance instant an hour later", "presented-value-wrong", passing, fixture, expectation, word, shifted);
            }
        }

        return NotEvaluable("E16", "print the acceptance instant an hour later", "presented-value-wrong");
    }

    private static string ReplaceLast(string text, char from, char to)
    {
        var index = text.LastIndexOf(from);
        return index < 0 ? text : text[..index] + to + text[(index + 1)..];
    }

    /// <summary>
    /// The controls of the page frame: a page of another size, a content word pushed
    /// into the margin, a wrong position, a wrong page count, a footer removed from one
    /// page and the column headers removed from a continued page.
    /// </summary>
    private static IEnumerable<ControlOutcome> PageFrameControls(
        PdfExtraction passing,
        FixtureSource fixture,
        ExpectationDocument expectation,
        PlacedDocument placed)
    {
        yield return ResizeAPage(passing, fixture, expectation);
        yield return PushAWordIntoTheMargin(passing, fixture, expectation, placed);
        yield return ChangeThePosition(passing, fixture, expectation);
        yield return ChangeThePageCount(passing, fixture, expectation);
        yield return RemoveTheFooter(passing, fixture, expectation);
        yield return RemoveTheColumnHeaders(passing, fixture, expectation, placed);
    }

    private static ControlOutcome ResizeAPage(PdfExtraction passing, FixtureSource fixture, ExpectationDocument expectation)
    {
        if (passing.Pages == 0)
        {
            return NotEvaluable("E9", "make one page two points wider", "page-geometry-wrong");
        }

        var sizes = passing.PageSizes.ToList();
        sizes[0] = (sizes[0].Width + 2.0, sizes[0].Height);
        var verdict = IntegrityRules.Judge(passing.WithPageSizes(sizes), fixture, expectation, "E9");
        var behaved = verdict.Failures.Any(failure => failure.Rule == "TI-9" && failure.Class == "page-geometry-wrong");

        return new ControlOutcome("E9", "make page 1 two points wider", "page-geometry-wrong", behaved, Described(verdict));
    }

    private static ControlOutcome PushAWordIntoTheMargin(
        PdfExtraction passing,
        FixtureSource fixture,
        ExpectationDocument expectation,
        PlacedDocument placed)
    {
        var word = placed.Lines
            .Where(line => line.Page == 1)
            .SelectMany(line => line.Words)
            .FirstOrDefault(candidate => candidate.XMin > ExpectationDocument.Points.Of(expectation.LeftMm) + 10.0);
        if (word is null)
        {
            return NotEvaluable("E10", "push a content word into the left margin", "frame-wrong");
        }

        var left = ExpectationDocument.Points.Of(expectation.LeftMm);
        var delta = word.XMin - (left - 3.0);
        var moved = word with { XMin = word.XMin - delta, XMax = word.XMax - delta };

        return Evaluate(
            "E10",
            "push a content word three points into the left margin",
            "frame-wrong",
            null,
            passing,
            fixture,
            expectation,
            list =>
            {
                var at = list.FindIndex(candidate => ReferenceEquals(candidate, word));
                if (at >= 0)
                {
                    list[at] = moved;
                }
            });
    }

    private static ControlOutcome ChangeThePosition(PdfExtraction passing, FixtureSource fixture, ExpectationDocument expectation)
    {
        if (passing.Pages < 2)
        {
            return NotEvaluable("E11", "print a wrong position on page 2", "numbering-wrong");
        }

        var word = NumberingWord(passing, expectation, page: 2, after: "Hoja");
        if (word is null || !int.TryParse(word.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var position))
        {
            return NotEvaluable("E11", "print a wrong position on page 2", "numbering-wrong");
        }

        return ReplaceWordText("E11", $"print position {position + 1} on page 2", "numbering-wrong", passing, fixture, expectation, word, (position + 1).ToString(CultureInfo.InvariantCulture));
    }

    private static ControlOutcome ChangeThePageCount(PdfExtraction passing, FixtureSource fixture, ExpectationDocument expectation)
    {
        var page = passing.Pages;
        var word = NumberingWord(passing, expectation, page, after: "de");
        if (word is null || !int.TryParse(word.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var pages))
        {
            return NotEvaluable("E12", "print a wrong page count on the last page", "numbering-wrong");
        }

        return ReplaceWordText("E12", $"print page count {pages + 1} on the last page", "numbering-wrong", passing, fixture, expectation, word, (pages + 1).ToString(CultureInfo.InvariantCulture));
    }

    private static ControlOutcome RemoveTheFooter(PdfExtraction passing, FixtureSource fixture, ExpectationDocument expectation)
    {
        var page = passing.Pages >= 2 ? 2 : 1;
        var words = passing.Words
            .Where(word => word.Page == page && word.YCentre > expectation.ContentBottomPt)
            .ToList();
        if (words.Count == 0)
        {
            return NotEvaluable("E13", "remove the footer from one page", "footer-wrong");
        }

        return Evaluate(
            "E13",
            $"remove the footer from page {page}",
            "footer-wrong",
            null,
            passing,
            fixture,
            expectation,
            list => list.RemoveAll(candidate => words.Any(word => ReferenceEquals(word, candidate))));
    }

    private static ControlOutcome RemoveTheColumnHeaders(
        PdfExtraction passing,
        FixtureSource fixture,
        ExpectationDocument expectation,
        PlacedDocument placed)
    {
        var headRow = Normalisation.N(string.Join(' ', expectation.HeadRow));
        var page = 3;
        var words = placed.Lines
            .Where(line => line.Page == page && Normalisation.N(line.Text) == headRow)
            .SelectMany(line => line.Words)
            .ToList();
        if (words.Count == 0)
        {
            return NotEvaluable("E14", "remove the column headers from page 3", "column-header-missing");
        }

        return Evaluate(
            "E14",
            "remove the column headers from page 3",
            "column-header-missing",
            null,
            passing,
            fixture,
            expectation,
            list => list.RemoveAll(candidate => words.Any(word => ReferenceEquals(word, candidate))));
    }

    // The word that prints the number after <paramref name="after"/> in the header band
    // of one page, read left to right: the position after the numbering's first word and
    // the page count after its last.
    private static PdfWord? NumberingWord(PdfExtraction passing, ExpectationDocument expectation, int page, string after)
    {
        if (expectation.PageNumbering is null)
        {
            return null;
        }

        var band = passing.Words
            .Where(word => word.Page == page && word.YCentre < expectation.ContentTopPt)
            .OrderBy(word => word.YCentre)
            .ThenBy(word => word.XMin)
            .ToList();
        var index = band.FindLastIndex(word => string.Equals(word.Text, after, StringComparison.Ordinal));
        return index >= 0 && index + 1 < band.Count ? band[index + 1] : null;
    }

    private static ControlOutcome ReplaceWordText(
        string id,
        string mutation,
        string expectedClass,
        PdfExtraction passing,
        FixtureSource fixture,
        ExpectationDocument expectation,
        PdfWord word,
        string text) =>
        Evaluate(
            id,
            mutation,
            expectedClass,
            null,
            passing,
            fixture,
            expectation,
            list =>
            {
                var at = list.FindIndex(candidate => ReferenceEquals(candidate, word));
                if (at >= 0)
                {
                    list[at] = word with { Text = text };
                }
            });

    /// <summary>The failures of a verdict as rule, kind and place — never a value.</summary>
    public static IReadOnlyList<string> Described(Verdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return
        [
            .. verdict.Failures.Select(failure => failure.Rule + " " + failure.Class
                + (failure.Row is null ? string.Empty : " row " + failure.Row.Value.ToString(CultureInfo.InvariantCulture))
                + (failure.Field is null ? string.Empty : " field " + failure.Field)
                + (failure.Page is null ? string.Empty : " page " + failure.Page.Value.ToString(CultureInfo.InvariantCulture))
                + (failure.Detail is null ? string.Empty : " (" + failure.Detail + ")")),
        ];
    }

    private static ControlOutcome DeleteADescriptionWord(PdfExtraction passing, FixtureSource fixture, ExpectationDocument expectation, ClassifiedDocument classified)
    {
        var row = Tallest(classified);
        var page = row.Pages.Max;
        var word = row.Words.Where(entry => entry.Column == TextColumn(expectation) && entry.Word.Page == page).Select(entry => entry.Word).FirstOrDefault();
        if (word is null)
        {
            return NotEvaluable("E1", "delete a description word", "text-dropped");
        }

        return Evaluate(
            "E1",
            $"delete a description word of row {row.Key} on page {page}",
            "text-dropped",
            row.Key,
            passing,
            fixture,
            expectation,
            list => list.RemoveAll(candidate => ReferenceEquals(candidate, word)));
    }

    private static ControlOutcome RepeatALine(PdfExtraction passing, FixtureSource fixture, ExpectationDocument expectation, PlacedDocument placed, ClassifiedDocument classified)
    {
        var row = Tallest(classified);
        var lastPage = row.Pages.Max;
        var line = placed.Lines.LastOrDefault(candidate => candidate.Page == lastPage
            && candidate.Words.All(word => row.Words.Any(entry => ReferenceEquals(entry.Word, word))));
        if (line is null || line.Words.Count == 0)
        {
            return NotEvaluable("E2", "copy a line of a row", "text-repeated");
        }

        var rowBottom = row.Words.Where(entry => entry.Word.Page == lastPage).Max(entry => entry.Word.YCentre);
        var delta = rowBottom + 8.0 - line.Words[0].YCentre;
        var clone = line.Words.Select(word => word with
        {
            YMin = word.YMin + delta,
            YMax = word.YMax + delta,
        }).ToList();

        return Evaluate(
            "E2",
            $"copy the last line of row {row.Key} below the row on page {lastPage}",
            "text-repeated",
            row.Key,
            passing,
            fixture,
            expectation,
            list => list.AddRange(clone));
    }

    private static ControlOutcome DeleteARow(PdfExtraction passing, FixtureSource fixture, ExpectationDocument expectation, ClassifiedDocument classified)
    {
        var row = Tallest(classified);
        var words = RowWords(row);

        return Evaluate(
            "E3",
            $"delete every word of row {row.Key}",
            "row-lost",
            row.Key,
            passing,
            fixture,
            expectation,
            list => list.RemoveAll(candidate => words.Any(word => ReferenceEquals(word, candidate))));
    }

    private static ControlOutcome SwapTwoWords(PdfExtraction passing, FixtureSource fixture, ExpectationDocument expectation, ClassifiedDocument classified)
    {
        var row = Tallest(classified);
        foreach (var line in row.Words.Where(entry => entry.Column == TextColumn(expectation)).GroupBy(entry => new { entry.Word.Page, entry.Word.YCentre }))
        {
            var words = line.Select(entry => entry.Word).ToList();
            for (var index = 0; index + 1 < words.Count; index++)
            {
                var first = words[index];
                var second = words[index + 1];
                if (string.Equals(first.Text, second.Text, StringComparison.Ordinal))
                {
                    continue;
                }

                return Evaluate(
                    "E4",
                    $"swap two adjacent description words of row {row.Key} on page {first.Page}",
                    "text-altered",
                    row.Key,
                    passing,
                    fixture,
                    expectation,
                    list =>
                    {
                        var firstAt = list.FindIndex(candidate => ReferenceEquals(candidate, first));
                        var secondAt = list.FindIndex(candidate => ReferenceEquals(candidate, second));
                        list[firstAt] = first with { Text = second.Text };
                        list[secondAt] = second with { Text = first.Text };
                    });
            }
        }

        return NotEvaluable("E4", "swap two description words", "text-altered");
    }

    private static ControlOutcome MoveARowOutOfOrder(PdfExtraction passing, FixtureSource fixture, ExpectationDocument expectation, ClassifiedDocument classified)
    {
        for (var index = 0; index + 1 < classified.Rows.Count; index++)
        {
            var first = classified.Rows[index];
            var second = classified.Rows[index + 1];
            if (first.AnchorPage != second.AnchorPage)
            {
                continue;
            }

            var firstWords = RowWords(first).Where(word => word.Page == first.AnchorPage).ToList();
            var secondBottom = second.Words.Where(entry => entry.Word.Page == first.AnchorPage).Max(entry => entry.Word.YCentre);
            var firstTop = firstWords.Min(word => word.YCentre);
            var delta = secondBottom + 20.0 - firstTop;
            if (firstWords.Max(word => word.YCentre) + delta > expectation.ContentBottomPt - 5.0)
            {
                continue;
            }

            var moved = firstWords.Select(word => word with { YMin = word.YMin + delta, YMax = word.YMax + delta }).ToList();
            var replaced = firstWords.Select((word, position) => (From: word, To: moved[position])).ToList();
            return Evaluate(
                "E5",
                $"move row {first.Key} below row {second.Key}",
                "row-order",
                null,
                passing,
                fixture,
                expectation,
                list =>
                {
                    for (var position = 0; position < list.Count; position++)
                    {
                        var match = replaced.FirstOrDefault(entry => ReferenceEquals(entry.From, list[position]));
                        if (match.From is not null)
                        {
                            list[position] = match.To;
                        }
                    }
                });
        }

        return NotEvaluable("E5", "move a row below the next one", "row-order");
    }

    private static ControlOutcome DuplicateARow(PdfExtraction passing, FixtureSource fixture, ExpectationDocument expectation, ClassifiedDocument classified)
    {
        var row = Tallest(classified);
        var words = RowWords(row).Where(word => word.Page == row.Pages.Min).ToList();
        var top = words.Min(word => word.YCentre);
        var bottom = words.Max(word => word.YCentre);
        var delta = bottom + 15.0 - top;
        if (bottom + delta > expectation.ContentBottomPt - 5.0)
        {
            delta = expectation.ContentTopPt + 10.0 - top;
        }

        var clone = words.Select(word => word with { YMin = word.YMin + delta, YMax = word.YMax + delta }).ToList();

        return Evaluate(
            "E6",
            $"print row {row.Key} a second time",
            "row-duplicated",
            row.Key,
            passing,
            fixture,
            expectation,
            list => list.AddRange(clone));
    }

    private static ControlOutcome BlankAPage(PdfExtraction passing, FixtureSource fixture, ExpectationDocument expectation)
    {
        if (passing.Pages < 2)
        {
            return NotEvaluable("E7", "delete every word of a page", "structure-lost");
        }

        var page = passing.Pages;
        var words = passing.Words.Where(word => word.Page == page).ToList();

        return Evaluate(
            "E7",
            $"delete every word of page {page}",
            "structure-lost",
            null,
            passing,
            fixture,
            expectation,
            list => list.RemoveAll(candidate => words.Any(word => ReferenceEquals(word, candidate))));
    }

    private static ControlOutcome PrintARecordTheDataDoesNotHold(PdfExtraction passing, FixtureSource fixture, ExpectationDocument expectation, PlacedDocument placed)
    {
        var anchor = placed.Lines.FirstOrDefault(line => Normalisation.N(line.Text) == Normalisation.N(string.Join(' ', expectation.HeadRow)))
            ?? placed.Lines.FirstOrDefault();
        if (anchor is null)
        {
            return NotEvaluable("E8", "print a record number the data does not hold", "row-order");
        }

        var held = fixture.Records.Select(record => record.Key).ToHashSet(StringComparer.Ordinal);
        var sequence = 1;
        while (held.Contains(sequence.ToString(CultureInfo.InvariantCulture)))
        {
            sequence++;
        }

        var injected = new PdfWord(
            anchor.Page,
            sequence.ToString(CultureInfo.InvariantCulture),
            ExpectationDocument.Points.Of(expectation.Columns[0].StartMm) + 2.0,
            anchor.Words[0].YMin + 3.0,
            ExpectationDocument.Points.Of(expectation.Columns[0].StartMm) + 12.0,
            anchor.Words[0].YMax + 3.0);

        return Evaluate(
            "E8",
            $"print record number {sequence}, which the data does not hold",
            "row-order",
            sequence.ToString(CultureInfo.InvariantCulture),
            passing,
            fixture,
            expectation,
            list => list.Add(injected));
    }

    /// <summary>
    /// The column a text control changes: the widest declared column that is neither the
    /// row key nor the amount — the cell that holds several words, where a word can be cut
    /// without disturbing the others.
    /// </summary>
    private static string TextColumn(ExpectationDocument expectation) =>
        expectation.Columns
            .Where(column => !string.Equals(column.Column, expectation.RowKey, StringComparison.Ordinal)
                && !string.Equals(column.Column, expectation.AmountColumn, StringComparison.Ordinal))
            .OrderByDescending(column => column.EndMm - column.StartMm)
            .Select(column => column.Column)
            .FirstOrDefault()
        ?? expectation.RowKey;

    private static ControlOutcome Evaluate(
        string id,
        string mutation,
        string expectedClass,
        string? expectedKey,
        PdfExtraction passing,
        FixtureSource fixture,
        ExpectationDocument expectation,
        Action<List<PdfWord>> change)
    {
        var expectedRow = int.TryParse(expectedKey, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : (int?)null;
        var words = passing.Words.ToList();
        change(words);
        var verdict = IntegrityRules.Judge(passing.WithWords(words), fixture, expectation, id);
        var behaved = verdict.Failures.Any(failure =>
            string.Equals(failure.Class, expectedClass, StringComparison.Ordinal)
            && (expectedKey is null || failure.Row == expectedRow));

        return new ControlOutcome(id, mutation, expectedClass, behaved, Described(verdict));
    }

    private static ControlOutcome NotEvaluable(string id, string mutation, string expected) =>
        new(id, mutation, expected, false, ["the control could not be placed on this document"]);

    private static ItemRow Tallest(ClassifiedDocument classified) =>
        classified.Rows.FirstOrDefault(row => row.Pages.Count > 1) ?? classified.Rows[0];

    private static List<PdfWord> RowWords(ItemRow row) =>
        [.. row.Words.Select(entry => entry.Word)];
}
