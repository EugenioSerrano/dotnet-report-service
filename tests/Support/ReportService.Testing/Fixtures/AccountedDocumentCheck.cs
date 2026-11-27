using System.Globalization;
using ReportService.Pdf.Verification;

namespace ReportService.Testing.Fixtures;

/// <summary>
/// The independent accounting judgment of one printed document: every page's band must
/// carry its three labelled values and they must equal the values computed from the source
/// records and the page attribution; every final group's total must print where the group
/// ends; every record whose row spans pages must carry the fixed continuation mark on its
/// earlier parts; the chain must close on the grand total; and, when the document is an
/// assembled one, every page must carry the global stamp in the reserved area with no
/// band word inside it. It shares no code with the product's verifier and returns the
/// problems it found, never a verdict of its own.
/// </summary>
public static class AccountedDocumentCheck
{
    /// <summary>The fixed mark a row's own amount cell carries on every part but its last.</summary>
    public const string ContinuesMark = "(continues)";

    /// <summary>Verifies <paramref name="reading"/> against the expected values.</summary>
    /// <param name="reading">The printed document as the reader read it.</param>
    /// <param name="declaration">The band, the scale, the culture and the numbering area.</param>
    /// <param name="expected">The expected values, computed from the source records and the attribution.</param>
    /// <param name="attribution">The page each row started on and the page it ended on.</param>
    /// <param name="expectNumbering">Whether every page must carry the global stamp (an assembled document).</param>
    public static IReadOnlyList<string> Verify(
        PdfReading reading,
        PageAccountingDeclaration declaration,
        PageAccountingExpected expected,
        PageAccountingAttribution attribution,
        bool expectNumbering)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(attribution);
        var problems = new List<string>();

        if (reading.Pages.Count != expected.Pages.Count)
        {
            problems.Add($"the document prints {reading.Pages.Count} page(s); the expectation holds {expected.Pages.Count}");
        }

        foreach (var page in reading.Pages)
        {
            if (page.Number < 1 || page.Number > expected.Pages.Count)
            {
                continue;
            }

            var lines = PageAccountingExpectations.ReadBand(page, declaration);
            if (lines.BroughtForward is null || lines.PageSubtotal is null || lines.CarriedForward is null)
            {
                problems.Add($"page {page.Number}: the band does not print its three labelled values");
                continue;
            }

            Compare(problems, page.Number, "brought forward", lines.BroughtForward, expected.Pages[page.Number - 1].BroughtForward, declaration);
            Compare(problems, page.Number, "page subtotal", lines.PageSubtotal, expected.Pages[page.Number - 1].Subtotal, declaration);
            Compare(problems, page.Number, "carried forward", lines.CarriedForward, expected.Pages[page.Number - 1].CarriedForward, declaration);

            var collision = page.Words
                .Where(word => word.Band == ReadingBand.Footer
                    && PageAccountingExpectations.InsideNumberingArea(word, declaration))
                .ToList();
            if (collision.Count > 0)
            {
                problems.Add($"page {page.Number}: {collision.Count} accounting word(s) print inside the numbering area");
            }

            if (expectNumbering)
            {
                var stamp = string.Join(' ', PageAccountingExpectations.NumberingWords(page, declaration));
                var wording = declaration.NumberingFormat
                    .Replace("{X}", page.Number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{Y}", reading.Pages.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
                if (!string.Equals(stamp, wording, StringComparison.Ordinal))
                {
                    problems.Add($"page {page.Number}: the numbering area holds \"{stamp}\" where \"{wording}\" was expected");
                }
            }
            else if (PageAccountingExpectations.NumberingWords(page, declaration).Count > 0)
            {
                problems.Add($"page {page.Number}: the numbering area prints words in a document that carries no stamp");
            }
        }

        foreach (var record in expected.Records)
        {
            if (!attribution.EndPages.ContainsKey(record.Ordinal))
            {
                problems.Add($"record {record.Ordinal}: its amount was not read from any page");
                continue;
            }

            if (!attribution.StartPages.TryGetValue(record.Ordinal, out var start))
            {
                continue;
            }

            var end = attribution.EndPages[record.Ordinal];
            for (var page = start; page < end; page++)
            {
                var carries = reading.Pages[page - 1].Words
                    .Where(word => word.Band == ReadingBand.Content)
                    .Any(word => string.Equals(Normalisation.Normalise(word.Text), Normalisation.Normalise(ContinuesMark), StringComparison.Ordinal));
                if (!carries)
                {
                    problems.Add($"record {record.Ordinal}: page {page} holds a non-final part without the continuation mark");
                }
            }
        }

        foreach (var group in expected.Groups)
        {
            if (!attribution.EndPages.TryGetValue(group.LastOrdinal, out var page))
            {
                problems.Add($"group {group.Key}: the page its last record ended on was not read");
                continue;
            }

            var text = expected.BandText(group.Total);
            var printed = reading.Pages[page - 1].Words
                .Where(word => word.Band == ReadingBand.Content)
                .Any(word => string.Equals(word.Text, text, StringComparison.Ordinal));
            if (!printed)
            {
                problems.Add($"group {group.Key}: the total {text} does not print on page {page} where the group ends");
            }
        }

        if (expected.Pages.Count > 0)
        {
            var last = expected.Pages[^1];
            if (last.CarriedForward != expected.GrandTotal + expected.Opening)
            {
                problems.Add($"the last page's carried forward is {last.CarriedForward}; the grand total is {expected.GrandTotal}");
            }
        }

        return problems;
    }

    private static void Compare(
        List<string> problems,
        int page,
        string label,
        string printed,
        decimal expected,
        PageAccountingDeclaration declaration)
    {
        if (!PageAccountingExpectations.TryParseBand(printed, declaration.Scale, declaration.Culture, out var value))
        {
            problems.Add($"page {page}: the {label} \"{printed}\" is not a value at scale {declaration.Scale} in {declaration.Culture}");
            return;
        }

        if (value != expected)
        {
            problems.Add($"page {page}: the {label} is {value}; the records give {expected}");
        }
    }
}
