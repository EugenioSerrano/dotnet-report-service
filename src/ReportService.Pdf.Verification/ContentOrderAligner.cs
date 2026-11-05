using System.Text;
using ReportService.Templates;

namespace ReportService.Pdf.Verification;

/// <summary>The reason words a check records. Every one of them fails the document it belongs to.</summary>
public static class VerificationReasons
{
    /// <summary>A whole record printed nowhere.</summary>
    public const string RowLost = "row-lost";

    /// <summary>A record printed twice, or one the partition does not hold.</summary>
    public const string RowExtra = "row-extra";

    /// <summary>The records printed in an order the data does not have.</summary>
    public const string RowOrder = "row-order";

    /// <summary>A record whose row prints no text at all.</summary>
    public const string RowWithoutText = "row-without-text";

    /// <summary>A value the package prints as it is does not appear as it is.</summary>
    public const string AnchorMismatch = "anchor-mismatch";

    /// <summary>Composed text that the document does not print.</summary>
    public const string TextDropped = "text-dropped";

    /// <summary>Printed text that is not the composed text.</summary>
    public const string TextAltered = "text-altered";

    /// <summary>Printed text the composition does not hold.</summary>
    public const string TextExtra = "text-extra";

    /// <summary>A document with no page at all.</summary>
    public const string NoPage = "no-page";

    /// <summary>A page that prints no word at all.</summary>
    public const string BlankPage = "blank-page";

    /// <summary>A document the reader cannot read.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>A character the reader could not map to a glyph.</summary>
    public const string UnmappedGlyph = "unmapped-glyph";

    /// <summary>An alignment that could not be decided within its budget.</summary>
    public const string AmbiguousAlignment = "ambiguous-alignment";

    /// <summary>The package declares no field it prints as it is, so nothing can be bound to the source.</summary>
    public const string AnchorsUndeclared = "anchors-undeclared";

    /// <summary>The package declares page subtotals, which this increment cannot print.</summary>
    public const string PageAccounting = "page-accounting";
}

/// <summary>What one alignment pass found.</summary>
/// <param name="Passed">Whether every unit and every page was consumed exactly once.</param>
/// <param name="Reason">The failure's reason word, or <see langword="null"/> when the pass succeeded.</param>
/// <param name="Page">The page of the first mismatch, from one.</param>
/// <param name="Offset">The offset in that page's content text where the mismatch starts.</param>
/// <param name="UnitIndex">The position of the unit the mismatch was found at.</param>
/// <param name="ConsumedCharacters">How many characters of each unit the pass consumed, by unit.</param>
public sealed record AlignmentOutcome(
    bool Passed,
    string? Reason,
    int? Page,
    int? Offset,
    int? UnitIndex,
    IReadOnlyList<int> ConsumedCharacters);

/// <summary>
/// Consumes the expected text units page by page, in the painting order the
/// document was printed in: every page's words are concatenated, the repeated head
/// of a table is consumed where the surface prints it again, and the units must
/// follow each other exactly - a unit may end at more than one position, so the
/// pass searches the split points longest first, depth first, and remembers the
/// states that failed. A unit that starts on one page and ends on the next is one
/// unit, not two: its continuation must be the first thing the next page prints.
/// </summary>
public sealed class ContentOrderAligner
{
    /// <summary>The number of character comparisons one block's alignment may spend.</summary>
    public const int DefaultBudget = 2_000_000;

    private readonly int budget;

    /// <summary>Creates an aligner that spends at most <paramref name="budget"/> comparisons per block.</summary>
    /// <param name="budget">The comparison budget, in characters.</param>
    public ContentOrderAligner(int budget = DefaultBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);
        this.budget = budget;
    }

    /// <summary>Aligns <paramref name="pages"/> against <paramref name="units"/>.</summary>
    /// <param name="pages">The content text of each page, normalised, in page order.</param>
    /// <param name="units">The expected units, in document order.</param>
    public AlignmentOutcome Align(IReadOnlyList<string> pages, IReadOnlyList<TextUnit> units)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(units);

        var search = new Search(pages, units, budget);
        if (search.Solve())
        {
            return new AlignmentOutcome(true, null, null, null, null, search.ConsumedCharacters());
        }

        return search.Failure();
    }

    /// <summary>One pass's state: the pages, the units, the failed states and the budget.</summary>
    private sealed class Search
    {
        private readonly IReadOnlyList<string> pages;
        private readonly IReadOnlyList<TextUnit> units;
        private readonly string head;
        private readonly string foot;
        private readonly HashSet<State> failed = [];
        private readonly int[] consumed;
        private readonly int budget;
        private int spent;
        private bool overBudget;

        public Search(IReadOnlyList<string> pages, IReadOnlyList<TextUnit> units, int budget)
        {
            this.pages = pages;
            this.units = units;
            this.budget = budget;
            consumed = new int[units.Count];
            head = string.Concat(units.Where(unit => unit.RepeatableHead).Select(unit => unit.Normalised));
            foot = string.Concat(units.Where(unit => unit.RepeatableFoot).Select(unit => unit.Normalised));
        }

        public bool Solve()
        {
            if (pages.Count == 0)
            {
                return units.Count == 0;
            }

            return ConsumePage(0, 0, 0);
        }

        public IReadOnlyList<int> ConsumedCharacters() => consumed;

        /// <summary>
        /// Consumes one page from its start, with the unit at <paramref name="cursor"/>
        /// open at <paramref name="openOffset"/> characters - no unit when the cursor is
        /// past the end.
        /// </summary>
        private bool ConsumePage(int page, int cursor, int openOffset)
        {
            var text = pages[page];
            foreach (var end in Ends(text, page == pages.Count - 1))
            {
                // The surface repeats a table's head at the top of every page after the
                // first; the units of that head were consumed where the document holds them.
                if (page > 0 && head.Length > 0 && text.StartsWith(head, StringComparison.Ordinal)
                    && Fit(page, head.Length, cursor, openOffset, end))
                {
                    return true;
                }

                if (Fit(page, 0, cursor, openOffset, end))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The end of the page's text: its whole text, and - when the surface repeats a
        /// foot at the bottom of a page that is not the last - the text before that foot.
        /// </summary>
        private IEnumerable<int> Ends(string text, bool last)
        {
            yield return text.Length;
            if (!last && foot.Length > 0 && text.Length > foot.Length && text.EndsWith(foot, StringComparison.Ordinal))
            {
                yield return text.Length - foot.Length;
            }
        }

        /// <summary>Consumes the rest of the page, starting with <paramref name="cursor"/> and its consumed prefix.</summary>
        private bool Fit(int page, int position, int cursor, int openOffset, int end)
        {
            if (overBudget)
            {
                return false;
            }

            var key = new State(page, position, cursor, openOffset, end);
            if (failed.Contains(key))
            {
                return false;
            }

            if (cursor >= units.Count)
            {
                if (position == end && (page == pages.Count - 1 || ConsumePage(page + 1, cursor, 0)))
                {
                    return true;
                }

                failed.Add(key);
                return false;
            }

            var text = pages[page];
            var remaining = units[cursor].Normalised;
            var available = Math.Min(remaining.Length - openOffset, end - position);
            for (var length = available; length >= 1; length--)
            {
                spent += length;
                if (spent > budget)
                {
                    overBudget = true;
                    return false;
                }

                if (!text.AsSpan(position, length).SequenceEqual(remaining.AsSpan(openOffset, length)))
                {
                    continue;
                }

                if (openOffset + length == remaining.Length)
                {
                    consumed[cursor] = openOffset + length;
                    if (Fit(page, position + length, cursor + 1, 0, end))
                    {
                        return true;
                    }

                    consumed[cursor] = 0;
                }
                else if (position + length == end && page < pages.Count - 1)
                {
                    consumed[cursor] = openOffset + length;
                    if (ConsumePage(page + 1, cursor, openOffset + length))
                    {
                        return true;
                    }

                    consumed[cursor] = 0;
                }
            }

            // Nothing more of the open unit fits: the page must end here for the unit to
            // continue on the next one.
            if (position == end && openOffset < remaining.Length && page < pages.Count - 1
                && ConsumePage(page + 1, cursor, openOffset))
            {
                consumed[cursor] = openOffset;
                return true;
            }

            failed.Add(key);
            return false;
        }

        /// <summary>
        /// The first position the greedy reading cannot pass, from which the failure's
        /// class is decided. The greedy reading is what a reader does by hand - consume
        /// the longest text that fits - so its stopping point is the one a person sees.
        /// </summary>
        public AlignmentOutcome Failure()
        {
            if (overBudget)
            {
                return new AlignmentOutcome(false, VerificationReasons.AmbiguousAlignment, null, null, null, consumed);
            }

            if (pages.Count == 0)
            {
                return new AlignmentOutcome(false, VerificationReasons.NoPage, null, null, null, consumed);
            }

            var page = 0;
            var position = 0;
            var cursor = 0;
            var openOffset = 0;

            while (page < pages.Count)
            {
                var text = pages[page];
                var last = page == pages.Count - 1;
                var end = Ends(text, last).First();
                if (page > 0 && head.Length > 0 && text.StartsWith(head, StringComparison.Ordinal))
                {
                    position = head.Length;
                }

                while (cursor < units.Count)
                {
                    var remaining = units[cursor].Normalised;

                    // The class of a mismatch is decided where the unit it belongs to
                    // starts: a whole row that is missing must not be reported as a word
                    // that changed inside it.
                    var unitStart = position;
                    var unitOpenOffset = openOffset;
                    var available = Math.Min(remaining.Length - openOffset, end - position);
                    var fitted = 0;
                    for (var length = available; length >= 1; length--)
                    {
                        if (text.AsSpan(position, length).SequenceEqual(remaining.AsSpan(openOffset, length)))
                        {
                            fitted = length;
                            break;
                        }
                    }

                    if (fitted == 0)
                    {
                        if (position == end)
                        {
                            break;
                        }

                        return Classify(page, unitStart, cursor, unitOpenOffset);
                    }

                    position += fitted;
                    openOffset += fitted;
                    if (openOffset == remaining.Length)
                    {
                        cursor++;
                        openOffset = 0;
                    }
                    else if (position < end)
                    {
                        return Classify(page, unitStart, cursor, unitOpenOffset);
                    }
                }

                if (position != end)
                {
                    return Classify(page, position, cursor, openOffset);
                }

                page++;
                position = 0;
                openOffset = 0;
            }

            return cursor < units.Count
                ? Classify(pages.Count - 1, pages[^1].Length, cursor, openOffset)
                : new AlignmentOutcome(false, VerificationReasons.AmbiguousAlignment, null, null, null, consumed);
        }

        /// <summary>
        /// The class of a mismatch at one position: a whole row that is followed by the
        /// next one was lost; text that reappears later was dropped; text that appears
        /// where the composition has none was added; anything else was altered.
        /// </summary>
        private AlignmentOutcome Classify(int page, int position, int cursor, int openOffset)
        {
            var printed = PrintedRemaining(page, position);
            var expected = ExpectedRemaining(cursor, openOffset);

            if (cursor < units.Count
                && units[cursor].Kind == FragmentKind.Row
                && units[cursor].IndexInFragment == 0
                && NextRowStartsWith(cursor, printed))
            {
                return Failure(page, position, cursor, VerificationReasons.RowLost);
            }

            if (printed.Length == 0 && expected.Length > 0)
            {
                return Failure(page, position, cursor, VerificationReasons.TextDropped);
            }

            if (printed.Length > 0 && expected.Length == 0)
            {
                return Failure(page, position, cursor, VerificationReasons.TextExtra);
            }

            // The two texts agree up to the point where they part; what follows on the
            // printed side, found later in the composition, is text the document dropped,
            // and what follows on the composed side, found later on the page, is text it
            // added. Neither being found means the text was altered in place.
            var common = CommonPrefix(printed, expected);
            var printedRest = printed[common..];
            var expectedRest = expected[common..];

            if (printedRest.Length > 0 && expectedRest.IndexOf(Window(printedRest), StringComparison.Ordinal) > 0)
            {
                return Failure(page, position, cursor, VerificationReasons.TextDropped);
            }

            if (expectedRest.Length > 0 && printedRest.IndexOf(Window(expectedRest), StringComparison.Ordinal) > 0)
            {
                return Failure(page, position, cursor, VerificationReasons.TextExtra);
            }

            return Failure(page, position, cursor, VerificationReasons.TextAltered);
        }

        private static int CommonPrefix(string left, string right)
        {
            var length = Math.Min(left.Length, right.Length);
            var index = 0;
            while (index < length && left[index] == right[index])
            {
                index++;
            }

            return index;
        }

        private static AlignmentOutcome Failure(int page, int position, int cursor, string reason) =>
            new(false, reason, page + 1, position, cursor, []);

        /// <summary>Whether the printed text continues with the first row after the one at the cursor.</summary>
        private bool NextRowStartsWith(int cursor, string printed)
        {
            for (var index = cursor + 1; index < units.Count; index++)
            {
                if (units[index].Kind != FragmentKind.Row)
                {
                    continue;
                }

                return printed.StartsWith(units[index].Normalised, StringComparison.Ordinal);
            }

            return false;
        }

        private static string Window(string text) => text.Length <= 32 ? text : text[..32];

        private string PrintedRemaining(int page, int position)
        {
            var builder = new StringBuilder();
            if (page < pages.Count)
            {
                builder.Append(pages[page].AsSpan(position));
                for (var next = page + 1; next < pages.Count; next++)
                {
                    builder.Append(pages[next]);
                }
            }

            return builder.ToString();
        }

        private string ExpectedRemaining(int cursor, int openOffset)
        {
            var builder = new StringBuilder();
            for (var index = cursor; index < units.Count; index++)
            {
                var text = units[index].Normalised;
                builder.Append(index == cursor ? text.AsSpan(openOffset) : text.AsSpan());
            }

            return builder.ToString();
        }

        private readonly record struct State(int Page, int Position, int Cursor, int OpenOffset, int End);
    }
}
