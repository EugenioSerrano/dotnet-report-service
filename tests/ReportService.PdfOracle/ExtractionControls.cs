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

        return
        [
            DeleteADescriptionWord(passing, fixture, expectation, classified),
            RepeatALine(passing, fixture, expectation, placed, classified),
            DeleteARow(passing, fixture, expectation, classified),
            SwapTwoWords(passing, fixture, expectation, classified),
            MoveARowOutOfOrder(passing, fixture, expectation, classified),
            DuplicateARow(passing, fixture, expectation, classified),
            BlankAPage(passing, fixture, expectation),
            PrintARecordTheDataDoesNotHold(passing, fixture, expectation, placed),
        ];
    }

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
        var word = row.Words.Where(entry => entry.Column == "description" && entry.Word.Page == page).Select(entry => entry.Word).FirstOrDefault();
        if (word is null)
        {
            return NotEvaluable("E1", "delete a description word", "text-dropped");
        }

        return Evaluate(
            "E1",
            $"delete a description word of row {row.Sequence} on page {page}",
            "text-dropped",
            row.Sequence,
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
            $"copy the last line of row {row.Sequence} below the row on page {lastPage}",
            "text-repeated",
            row.Sequence,
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
            $"delete every word of row {row.Sequence}",
            "row-lost",
            row.Sequence,
            passing,
            fixture,
            expectation,
            list => list.RemoveAll(candidate => words.Any(word => ReferenceEquals(word, candidate))));
    }

    private static ControlOutcome SwapTwoWords(PdfExtraction passing, FixtureSource fixture, ExpectationDocument expectation, ClassifiedDocument classified)
    {
        var row = Tallest(classified);
        foreach (var line in row.Words.Where(entry => entry.Column == "description").GroupBy(entry => new { entry.Word.Page, entry.Word.YCentre }))
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
                    $"swap two adjacent description words of row {row.Sequence} on page {first.Page}",
                    "text-altered",
                    row.Sequence,
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
                $"move row {first.Sequence} below row {second.Sequence}",
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
            $"print row {row.Sequence} a second time",
            "row-duplicated",
            row.Sequence,
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

        var held = fixture.Records.Select(record => record.Sequence).ToHashSet();
        var sequence = 1;
        while (held.Contains(sequence))
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
            sequence,
            passing,
            fixture,
            expectation,
            list => list.Add(injected));
    }

    private static ControlOutcome Evaluate(
        string id,
        string mutation,
        string expectedClass,
        int? expectedRow,
        PdfExtraction passing,
        FixtureSource fixture,
        ExpectationDocument expectation,
        Action<List<PdfWord>> change)
    {
        var words = passing.Words.ToList();
        change(words);
        var verdict = IntegrityRules.Judge(passing.WithWords(words), fixture, expectation, id);
        var behaved = verdict.Failures.Any(failure =>
            string.Equals(failure.Class, expectedClass, StringComparison.Ordinal)
            && (expectedRow is null || failure.Row == expectedRow));

        return new ControlOutcome(id, mutation, expectedClass, behaved, Described(verdict));
    }

    private static ControlOutcome NotEvaluable(string id, string mutation, string expected) =>
        new(id, mutation, expected, false, ["the control could not be placed on this document"]);

    private static ItemRow Tallest(ClassifiedDocument classified) =>
        classified.Rows.FirstOrDefault(row => row.Pages.Count > 1) ?? classified.Rows[0];

    private static List<PdfWord> RowWords(ItemRow row) =>
        [.. row.NumberWords, .. row.Words.Select(entry => entry.Word)];
}
