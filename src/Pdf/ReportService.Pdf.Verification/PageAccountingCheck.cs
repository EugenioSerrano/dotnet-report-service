using System.Globalization;
using ReportService.Templates;

namespace ReportService.Pdf.Verification;

/// <summary>
/// What the page-accounting check found: a pass, or the reason and the position - the
/// page, the record ordinal or the footer label - and never a value of the report.
/// </summary>
/// <param name="Reason">The reason word, or <see langword="null"/> when the check passed.</param>
/// <param name="Page">The page of the failure, from one.</param>
/// <param name="RecordOrdinal">The record of the failure, or <see langword="null"/>.</param>
/// <param name="Label">The footer label of the failure, or <see langword="null"/>.</param>
public sealed record AccountingVerdict(string? Reason, int? Page = null, long? RecordOrdinal = null, string? Label = null)
{
    /// <summary>The check passed.</summary>
    public static AccountingVerdict Pass { get; } = new(Reason: null);

    /// <summary>Whether the check passed.</summary>
    public bool Passed => Reason is null;

    /// <summary>A failure with <paramref name="reason"/> at <paramref name="page"/>.</summary>
    /// <param name="reason">The reason word.</param>
    /// <param name="page">The page of the failure.</param>
    /// <param name="recordOrdinal">The record of the failure.</param>
    /// <param name="label">The footer label of the failure.</param>
    public static AccountingVerdict Fail(string reason, int? page = null, long? recordOrdinal = null, string? label = null) =>
        new(reason, page, recordOrdinal, label);
}

/// <summary>
/// The page-accounting check of ADR-021 §3.6: every page's subtotal, brought and
/// carried forward, every final group's total and every row's amount and continuation
/// mark, compared with values computed from the block's own records and the partition's
/// recorded opening - never from the layout engine's output or the composer's text. A
/// split row is counted once, on the page its last unit printed on. The check also reads
/// the reserved band: every page must print its three labelled lines, and no accounting
/// word may sit inside the global page-numbering area.
/// </summary>
public sealed class PageAccountingCheck
{
    /// <summary>The check's verdict for <paramref name="input"/>.</summary>
    /// <param name="input">The printed document, its alignment and the accounting contract.</param>
    /// <param name="reading">The printed document as the reader read it.</param>
    /// <param name="alignment">The order check's result, whose per-unit pages locate every row's end.</param>
    public AccountingVerdict Check(PrintedCheckInput input, PdfReading reading, AlignmentOutcome alignment)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(alignment);
        if (input.Accounting is not { } accounting)
        {
            return AccountingVerdict.Pass;
        }

        if (!TryCulture(accounting.Culture, out var culture))
        {
            return AccountingVerdict.Fail(VerificationReasons.AccountingAmountInvalid);
        }

        // The block's own amounts, read from the source values, in record order.
        var amounts = new List<(long Ordinal, decimal Amount, string Text)>(accounting.Block.Records.Count);
        foreach (var record in accounting.Block.Records)
        {
            if (!TryReadAmount(accounting.Block, record, accounting.AmountField, out var amount)
                || !AccountingReading.TryFormat(amount, accounting.Scale, culture, out var text))
            {
                return AccountingVerdict.Fail(VerificationReasons.AccountingAmountInvalid, recordOrdinal: record.Ordinal);
            }

            amounts.Add((record.Ordinal, amount, text));
        }

        // The chain closes on the partition's exact prefix sum: the block's own total is
        // what the partition recorded, never a recomputation of the layout.
        var blockSum = amounts.Sum(entry => entry.Amount);
        if (accounting.Index.BlockAmountTotal is { } recorded && recorded != blockSum)
        {
            return AccountingVerdict.Fail(VerificationReasons.AccountingChainWrong);
        }

        // Every record's printed pages, from the order check's own map.
        var pages = alignment.Pages ?? [];
        var printedPagesOfRecord = new Dictionary<long, List<int>>();
        for (var unit = 0; unit < input.Text.Units.Count && unit < pages.Count; unit++)
        {
            var text = input.Text.Units[unit];
            if (text.Kind != FragmentKind.Row || pages[unit] <= 0)
            {
                continue;
            }

            if (!printedPagesOfRecord.TryGetValue(text.RecordOrdinal, out var list))
            {
                list = [];
                printedPagesOfRecord[text.RecordOrdinal] = list;
            }

            if (!list.Contains(pages[unit]))
            {
                list.Add(pages[unit]);
            }
        }

        var endPage = new Dictionary<long, int>();
        foreach (var (ordinal, _, _) in amounts)
        {
            if (printedPagesOfRecord.TryGetValue(ordinal, out var where) && where.Count > 0)
            {
                endPage[ordinal] = where.Max();
            }
        }

        // The expected chain, page by page, from the block's amounts and the partition's opening.
        var opening = accounting.Index.OpeningCarriedForward ?? 0m;
        var expectedBrought = new Dictionary<int, decimal>();
        var expectedCarried = new Dictionary<int, decimal>();
        var expectedSubtotal = new Dictionary<int, decimal>();
        var brought = opening;
        for (var page = 1; page <= reading.Pages.Count; page++)
        {
            var subtotal = amounts
                .Where(entry => endPage.TryGetValue(entry.Ordinal, out var end) && end == page)
                .Sum(entry => entry.Amount);
            expectedBrought[page] = brought;
            expectedSubtotal[page] = subtotal;
            brought += subtotal;
            expectedCarried[page] = brought;
        }

        // The printed band: three labelled values on every page, read under the declared scale.
        foreach (var page in reading.Pages)
        {
            var lines = AccountingReading.ReadFooter(page, accounting.Footer);
            if (!lines.Complete)
            {
                return AccountingVerdict.Fail(VerificationReasons.AccountingFooterMissing, page.Number);
            }

            if (!Read(lines.BroughtForward, accounting.Scale, culture, out var printedBrought)
                || !Read(lines.PageSubtotal, accounting.Scale, culture, out var printedSubtotal)
                || !Read(lines.CarriedForward, accounting.Scale, culture, out var printedCarried))
            {
                return AccountingVerdict.Fail(VerificationReasons.AccountingAmountInvalid, page.Number);
            }

            if (printedBrought != expectedBrought[page.Number])
            {
                return AccountingVerdict.Fail(VerificationReasons.BroughtForwardWrong, page.Number, label: "broughtForward");
            }

            if (printedSubtotal != expectedSubtotal[page.Number])
            {
                return AccountingVerdict.Fail(VerificationReasons.PageSubtotalWrong, page.Number, label: "pageSubtotal");
            }

            if (printedCarried != expectedCarried[page.Number])
            {
                return AccountingVerdict.Fail(VerificationReasons.CarriedForwardWrong, page.Number, label: "carriedForward");
            }

            if (accounting.NumberingArea is { } area
                && page.Words.Any(word => word.Band == ReadingBand.Footer
                    && AccountingReading.IsInside(
                        word,
                        area.LeftMm * PageFrame.PointsPerMillimetre,
                        area.TopMm * PageFrame.PointsPerMillimetre,
                        area.WidthMm * PageFrame.PointsPerMillimetre,
                        area.HeightMm * PageFrame.PointsPerMillimetre)))
            {
                return AccountingVerdict.Fail(VerificationReasons.AccountingNumberingCollision, page.Number);
            }
        }

        // The amounts and the marks, on the pages the rows really printed on.
        var duplicateAmounts = amounts
            .GroupBy(entry => entry.Text, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var mark = Normalisation.Normalise(AccountingReading.ContinuesMark);
        foreach (var (ordinal, _, text) in amounts)
        {
            if (!printedPagesOfRecord.TryGetValue(ordinal, out var where) || where.Count == 0 || !endPage.TryGetValue(ordinal, out var lastPage))
            {
                return AccountingVerdict.Fail(VerificationReasons.RowWithoutText, recordOrdinal: ordinal);
            }

            var normalised = Normalisation.Normalise(text);
            var end = reading.Pages[lastPage - 1];
            if (!HasWord(end, normalised))
            {
                return AccountingVerdict.Fail(VerificationReasons.AccountingAmountInvalid, end.Number, ordinal);
            }

            foreach (var pageNumber in where.Where(number => number < lastPage))
            {
                var page = reading.Pages[pageNumber - 1];
                if (!HasWord(page, mark))
                {
                    return AccountingVerdict.Fail(VerificationReasons.AccountingMarkWrong, pageNumber, ordinal);
                }

                if (!duplicateAmounts.Contains(text) && HasWord(page, normalised))
                {
                    return AccountingVerdict.Fail(VerificationReasons.AccountingMarkWrong, pageNumber, ordinal);
                }
            }
        }

        // Each final group's total, from its own rows plus the opening the partition recorded.
        if (!string.IsNullOrEmpty(accounting.GroupKey) && accounting.Block.Records.Count > 0)
        {
            if (accounting.Index.ContinuesPreviousGroup && accounting.Index.OpeningGroupAccumulated is null)
            {
                return AccountingVerdict.Fail(VerificationReasons.AccountingIndexIncomplete);
            }

            var byOrdinal = amounts.ToDictionary(entry => entry.Ordinal, entry => entry.Amount);
            foreach (var group in Groups(accounting, byOrdinal))
            {
                var total = group.Sum;
                if (group.IsFirst
                    && accounting.Index.ContinuesPreviousGroup
                    && accounting.Index.OpeningGroupAccumulated is { } carried
                    && string.Equals(group.Key, accounting.Index.OpeningGroupKey, StringComparison.Ordinal))
                {
                    total += carried;
                }

                if (!AccountingReading.TryFormat(total, accounting.Scale, culture, out var expected))
                {
                    return AccountingVerdict.Fail(VerificationReasons.AccountingAmountInvalid);
                }

                var page = group.EndOrdinal is { } lastOrdinal && endPage.TryGetValue(lastOrdinal, out var found) ? found : 0;
                if (group.Continues)
                {
                    // A group that continues into the next block prints no total at this
                    // block's end: its own partial sum must not appear where it ends.
                    if (page != 0 && HasWord(reading.Pages[page - 1], Normalisation.Normalise(expected)))
                    {
                        return AccountingVerdict.Fail(VerificationReasons.AccountingGroupWrong, page, group.EndOrdinal);
                    }

                    continue;
                }

                if (page == 0 || !HasWord(reading.Pages[page - 1], Normalisation.Normalise(expected)))
                {
                    return AccountingVerdict.Fail(VerificationReasons.AccountingGroupWrong, page == 0 ? null : page, group.EndOrdinal);
                }
            }
        }

        return AccountingVerdict.Pass;
    }

    /// <summary>One group of the block, as the printed page shows it.</summary>
    private sealed record Group(string Key, decimal Sum, long? EndOrdinal, bool Continues, bool IsFirst);

    private static List<Group> Groups(AccountingCheckInput accounting, IReadOnlyDictionary<long, decimal> byOrdinal)
    {
        var groups = new List<Group>();
        string? key = null;
        var sum = 0m;
        long? end = null;
        foreach (var record in accounting.Block.Records)
        {
            var recordKey = GroupKeyOf(record, accounting.GroupKey);
            if (groups.Count == 0 && end is null)
            {
                key = recordKey;
            }
            else if (!string.Equals(key, recordKey, StringComparison.Ordinal))
            {
                groups.Add(new Group(key ?? string.Empty, sum, end, Continues: false, IsFirst: groups.Count == 0));
                key = recordKey;
                sum = 0m;
            }

            if (byOrdinal.TryGetValue(record.Ordinal, out var value))
            {
                sum += value;
            }

            end = record.Ordinal;
        }

        groups.Add(new Group(key ?? string.Empty, sum, end, accounting.Index.ContinuesNextGroup, IsFirst: groups.Count == 0));
        return groups;
    }

    private static string? GroupKeyOf(BlockRecord record, string? groupKey)
    {
        if (groupKey is null || !record.Values.TryGetValue(groupKey, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static bool TryReadAmount(BlockFacts block, BlockRecord record, string amountField, out decimal amount)
    {
        amount = 0m;
        if (!record.Values.TryGetValue(amountField, out var value) || value is null)
        {
            return false;
        }

        return TryConvert(value, out amount);
    }

    private static bool TryConvert(object value, out decimal amount)
    {
        amount = 0m;
        switch (value)
        {
            case decimal exact:
                amount = exact;
                return true;
            case int integer:
                amount = integer;
                return true;
            case long large:
                amount = large;
                return true;
            case string text:
                return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
            case double real:
                // A value the block reader did not preserve exactly is refused rather than
                // rounded: a total is exact or it is not printed.
                return false;
            default:
                return false;
        }
    }

    private static bool TryCulture(string? name, out CultureInfo culture)
    {
        culture = CultureInfo.InvariantCulture;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            culture = CultureInfo.GetCultureInfo(name);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static bool Read(string? text, int scale, CultureInfo culture, out decimal value) =>
        AccountingReading.TryParse(text, scale, culture, out value);

    private static bool HasWord(PrintedPage page, string normalised)
    {
        foreach (var word in page.Words)
        {
            if (word.Band == ReadingBand.Content
                && string.Equals(Normalisation.Normalise(word.Text), normalised, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
