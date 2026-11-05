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
/// follow each other exactly. A unit that is not finished on a page stays open and
/// continues on the next one, after a later cell of its own row - the surface prints
/// the cells of a row that fit on the page before it continues a cell that does not.
/// A unit may end at more than one position, so the pass searches the split points
/// longest first, depth first, and remembers the states that failed.
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

    /// <summary>One unit that was started and not finished, and how much of it was consumed.</summary>
    private readonly record struct OpenUnit(int Unit, int Offset);

    /// <summary>
    /// The units a page started but did not finish, in document order. A unit stays
    /// open while the later cells of its own row print on the page it began on, and it
    /// continues - in this order - on the page that follows.
    /// </summary>
    private sealed class OpenUnits : IEquatable<OpenUnits>
    {
        /// <summary>No open unit.</summary>
        public static OpenUnits None { get; } = new([]);

        private readonly OpenUnit[] open;

        private OpenUnits(OpenUnit[] open) => this.open = open;

        /// <summary>Whether every started unit was finished.</summary>
        public bool IsEmpty => open.Length == 0;

        /// <summary>The first unit to continue, in document order.</summary>
        public OpenUnit First => open[0];

        /// <summary>The last unit started, which fixes the row the open units belong to.</summary>
        public OpenUnit Last => open[^1];

        /// <summary>The same list without its first unit.</summary>
        public OpenUnits CloseFirst() => open.Length == 1 ? None : new OpenUnits(open[1..]);

        /// <summary>The same list with its first unit's consumed length replaced.</summary>
        /// <param name="offset">How much of the first unit was consumed.</param>
        public OpenUnits WithFirstOffset(int offset)
        {
            var copy = (OpenUnit[])open.Clone();
            copy[0] = new OpenUnit(copy[0].Unit, offset);
            return new OpenUnits(copy);
        }

        /// <summary>The same list with <paramref name="unit"/> started after its last unit.</summary>
        /// <param name="unit">The unit to start.</param>
        public OpenUnits Append(OpenUnit unit)
        {
            var copy = new OpenUnit[open.Length + 1];
            open.CopyTo(copy, 0);
            copy[^1] = unit;
            return new OpenUnits(copy);
        }

        /// <inheritdoc />
        public bool Equals(OpenUnits? other)
        {
            if (other is null || other.open.Length != open.Length)
            {
                return false;
            }

            for (var index = 0; index < open.Length; index++)
            {
                if (open[index] != other.open[index])
                {
                    return false;
                }
            }

            return true;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as OpenUnits);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var unit in open)
            {
                hash.Add(unit.Unit);
                hash.Add(unit.Offset);
            }

            return hash.ToHashCode();
        }
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

            return ConsumePage(0, 0, OpenUnits.None);
        }

        public IReadOnlyList<int> ConsumedCharacters() => consumed;

        /// <summary>
        /// Consumes one page from its start, with the units an earlier page left open
        /// and the position of the first unit not started yet. This is the search's only
        /// recursive step: it moves to the document's next page, so the call depth is the
        /// number of pages the block prints and never the number of units they hold.
        /// </summary>
        private bool ConsumePage(int page, int cursor, OpenUnits open)
        {
            var text = pages[page];
            foreach (var end in Ends(text, page == pages.Count - 1))
            {
                // The surface repeats a table's head at the top of every page after the
                // first; the units of that head were consumed where the document holds them.
                if (page > 0 && head.Length > 0 && text.StartsWith(head, StringComparison.Ordinal)
                    && SearchPage(page, head.Length, cursor, open, end))
                {
                    return true;
                }

                if (SearchPage(page, 0, cursor, open, end))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Consumes one page from <paramref name="position"/> to <paramref name="end"/>: the open
        /// units continue where they left off, a later cell of an open unit's own row may print
        /// first, and the units not started yet follow in document order. The alternatives are
        /// searched longest first and depth first from this method's own stack, in the order the
        /// document prints them, so the page costs one call whatever its height.
        /// </summary>
        private bool SearchPage(int page, int position, int cursor, OpenUnits open, int end)
        {
            var text = pages[page];
            var stack = new List<Frame>();
            if (!TryEnter(page, position, cursor, open, end, undoUnit: -1, out var first))
            {
                return false;
            }

            stack.Add(first!);
            while (stack.Count > 0)
            {
                var frame = stack[^1];
                if (overBudget)
                {
                    Drain(stack, page, end);
                    return false;
                }

                if (frame.Position == end)
                {
                    stack.RemoveAt(stack.Count - 1);
                    if (page == pages.Count - 1)
                    {
                        if (frame.Open.IsEmpty && frame.Cursor >= units.Count)
                        {
                            return true;
                        }
                    }
                    else if (ConsumePage(page + 1, frame.Cursor, frame.Open))
                    {
                        return true;
                    }

                    Abandon(page, end, frame);
                    continue;
                }

                if (!NextCandidate(frame))
                {
                    stack.RemoveAt(stack.Count - 1);
                    Abandon(page, end, frame);
                    continue;
                }

                var length = frame.CandidateLength;
                if (!Spend(length))
                {
                    Drain(stack, page, end);
                    return false;
                }

                if (!text.AsSpan(frame.Position, length).SequenceEqual(frame.Remaining.AsSpan(frame.Offset, length)))
                {
                    continue;
                }

                if (frame.Phase == Frame.ContinueOpen)
                {
                    if (frame.Offset + length == frame.Remaining.Length)
                    {
                        var unit = frame.Unit;
                        consumed[unit] = frame.Offset + length;
                        if (TryEnter(page, frame.Position + length, frame.Cursor, frame.Open.CloseFirst(), end, unit, out var closed))
                        {
                            stack.Add(closed!);
                        }
                        else
                        {
                            consumed[unit] = 0;
                        }

                        continue;
                    }

                    if (frame.Position + length == end && page < pages.Count - 1)
                    {
                        if (ConsumePage(page + 1, frame.Cursor, frame.Open.WithFirstOffset(frame.Offset + length)))
                        {
                            return true;
                        }

                        continue;
                    }

                    if (TryEnter(page, frame.Position + length, frame.Cursor, frame.Open.WithFirstOffset(frame.Offset + length), end, undoUnit: -1, out var continued))
                    {
                        stack.Add(continued!);
                    }

                    continue;
                }

                if (length == frame.Remaining.Length)
                {
                    var unit = frame.Cursor;
                    consumed[unit] = length;
                    if (TryEnter(page, frame.Position + length, frame.Cursor + 1, frame.Open, end, unit, out var started))
                    {
                        stack.Add(started!);
                    }
                    else
                    {
                        consumed[unit] = 0;
                    }

                    continue;
                }

                if (frame.Position + length == end && page < pages.Count - 1)
                {
                    if (ConsumePage(page + 1, frame.Cursor + 1, frame.Open.Append(new OpenUnit(frame.Cursor, length))))
                    {
                        return true;
                    }

                    continue;
                }

                if (TryEnter(page, frame.Position + length, frame.Cursor + 1, frame.Open.Append(new OpenUnit(frame.Cursor, length)), end, undoUnit: -1, out var next))
                {
                    stack.Add(next!);
                }
            }

            return false;
        }

        /// <summary>
        /// Enters one state of a page's search, unless it already failed; its first alternative
        /// is fixed here, longest first.
        /// </summary>
        private bool TryEnter(int page, int position, int cursor, OpenUnits open, int end, int undoUnit, out Frame? frame)
        {
            frame = null;
            if (failed.Contains(new State(page, position, cursor, end, open)))
            {
                return false;
            }

            var entered = new Frame(position, cursor, open, end, undoUnit);
            if (open.IsEmpty)
            {
                entered.Phase = Frame.StartNext;
                PrepareStart(entered);
            }
            else
            {
                entered.Phase = Frame.ContinueOpen;
                entered.Unit = open.First.Unit;
                entered.Offset = open.First.Offset;
                entered.Remaining = units[entered.Unit].Normalised;
                entered.NextLength = Math.Min(entered.Remaining.Length - entered.Offset, end - position);
            }

            frame = entered;
            return true;
        }

        /// <summary>The state's next alternative, or false when it has none left.</summary>
        private bool NextCandidate(Frame frame)
        {
            while (true)
            {
                if (frame.NextLength >= 1)
                {
                    frame.CandidateLength = frame.NextLength;
                    frame.NextLength--;
                    return true;
                }

                if (frame.Phase == Frame.ContinueOpen)
                {
                    frame.Phase = Frame.StartNext;
                    PrepareStart(frame);
                    continue;
                }

                return false;
            }
        }

        /// <summary>Fixes the alternatives of the unit that has not started yet.</summary>
        private void PrepareStart(Frame frame)
        {
            frame.Unit = frame.Cursor;
            frame.Offset = 0;
            if (frame.Cursor >= units.Count
                || (!frame.Open.IsEmpty && units[frame.Cursor].FragmentIndex != units[frame.Open.Last.Unit].FragmentIndex))
            {
                frame.Remaining = string.Empty;
                frame.NextLength = 0;
                return;
            }

            frame.Remaining = units[frame.Cursor].Normalised;
            frame.NextLength = Math.Min(frame.Remaining.Length, frame.End - frame.Position);
        }

        /// <summary>Records that the state failed, and takes back the unit its entry closed.</summary>
        private void Abandon(int page, int end, Frame frame)
        {
            failed.Add(new State(page, frame.Position, frame.Cursor, end, frame.Open));
            if (frame.UndoUnit >= 0)
            {
                consumed[frame.UndoUnit] = 0;
            }
        }

        /// <summary>
        /// Takes back every state the page's search still holds, innermost first, exactly as the
        /// recursive form unwound when the comparison budget ran out.
        /// </summary>
        private void Drain(List<Frame> stack, int page, int end)
        {
            while (stack.Count > 0)
            {
                var frame = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                Abandon(page, end, frame);
            }
        }

        private bool Spend(int length)
        {
            spent += length;
            if (spent > budget)
            {
                overBudget = true;
                return false;
            }

            return true;
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
            var open = OpenUnits.None;
            var trackedUnit = -1;
            var unitStart = 0;
            var unitStartOffset = 0;

            while (page < pages.Count)
            {
                var text = pages[page];
                var end = Ends(text, page == pages.Count - 1).First();
                position = page > 0 && head.Length > 0 && text.StartsWith(head, StringComparison.Ordinal)
                    ? head.Length
                    : 0;
                trackedUnit = -1;

                while (position < end)
                {
                    // The unit that must print now: an open one continues before a new one
                    // starts, and a later cell of its row may print before it continues.
                    var unitIndex = open.IsEmpty ? cursor : open.First.Unit;
                    var offset = open.IsEmpty ? 0 : open.First.Offset;
                    if (unitIndex >= units.Count)
                    {
                        return Classify(page, position, cursor, 0);
                    }

                    if (unitIndex != trackedUnit)
                    {
                        trackedUnit = unitIndex;
                        unitStart = position;
                        unitStartOffset = offset;
                    }

                    var remaining = units[unitIndex].Normalised;
                    var fitted = LongestPrefix(text, position, end, remaining, offset);
                    if (fitted == 0)
                    {
                        if (open.IsEmpty || cursor >= units.Count
                            || units[cursor].FragmentIndex != units[open.Last.Unit].FragmentIndex)
                        {
                            return Classify(page, unitStart, unitIndex, unitStartOffset);
                        }

                        var cell = units[cursor].Normalised;
                        var started = LongestPrefix(text, position, end, cell, 0);
                        if (started == 0)
                        {
                            return Classify(page, position, cursor, 0);
                        }

                        position += started;
                        open = started == cell.Length ? open : open.Append(new OpenUnit(cursor, started));
                        cursor++;
                        continue;
                    }

                    position += fitted;
                    if (offset + fitted < remaining.Length)
                    {
                        var wasOpen = !open.IsEmpty;
                        open = wasOpen
                            ? open.WithFirstOffset(offset + fitted)
                            : open.Append(new OpenUnit(unitIndex, fitted));
                        if (!wasOpen)
                        {
                            cursor++;
                        }

                        continue;
                    }

                    var closedAnOpenUnit = !open.IsEmpty;
                    open = closedAnOpenUnit ? open.CloseFirst() : open;
                    if (!closedAnOpenUnit)
                    {
                        cursor++;
                    }
                }

                page++;
            }

            return !open.IsEmpty || cursor < units.Count
                ? Classify(pages.Count - 1, pages[^1].Length, open.IsEmpty ? cursor : open.First.Unit, open.IsEmpty ? 0 : open.First.Offset)
                : new AlignmentOutcome(false, VerificationReasons.AmbiguousAlignment, null, null, null, consumed);
        }

        /// <summary>The longest prefix of a unit's remaining text that the page prints here.</summary>
        private static int LongestPrefix(string text, int position, int end, string remaining, int offset)
        {
            var available = Math.Min(remaining.Length - offset, end - position);
            for (var length = available; length >= 1; length--)
            {
                if (text.AsSpan(position, length).SequenceEqual(remaining.AsSpan(offset, length)))
                {
                    return length;
                }
            }

            return 0;
        }

        /// <summary>
        /// The class of a mismatch at one position: a whole row that is followed by the
        /// next one was lost, a row that starts again where the next one belongs was
        /// printed twice, text that reappears later was dropped, text that appears where
        /// the composition has none was added, anything else was altered.
        /// </summary>
        private AlignmentOutcome Classify(int page, int position, int cursor, int openOffset)
        {
            var printed = PrintedRemaining(page, position);
            var expected = ExpectedRemaining(cursor, openOffset);

            if (cursor < units.Count
                && units[cursor].Kind == FragmentKind.Row
                && units[cursor].IndexInFragment == 0
                && openOffset == 0
                && WholeRowIsMissing(cursor, printed))
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

        /// <summary>
        /// Whether the whole row at <paramref name="cursor"/> is absent: the printed text
        /// continues with the row that follows it, or nothing is printed after it at all.
        /// </summary>
        private bool WholeRowIsMissing(int cursor, string printed)
        {
            if (printed.Length == 0)
            {
                return true;
            }

            for (var index = cursor + 1; index < units.Count; index++)
            {
                if (units[index].FragmentIndex == units[cursor].FragmentIndex
                    || units[index].Kind != FragmentKind.Row
                    || units[index].IndexInFragment != 0)
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

        private readonly record struct State(int Page, int Position, int Cursor, int End, OpenUnits Open);

        /// <summary>
        /// One state of a page's search and the alternatives it has left: the units an earlier
        /// page left open continue first, and the unit that has not started follows.
        /// </summary>
        private sealed class Frame
        {
            /// <summary>The alternatives of the units an earlier page left open.</summary>
            public const int ContinueOpen = 0;

            /// <summary>The alternatives of the unit that has not started yet.</summary>
            public const int StartNext = 1;

            public Frame(int position, int cursor, OpenUnits open, int end, int undoUnit)
            {
                Position = position;
                Cursor = cursor;
                Open = open;
                End = end;
                UndoUnit = undoUnit;
            }

            /// <summary>The position of the page the state stands at.</summary>
            public int Position { get; }

            /// <summary>The first unit that has not started.</summary>
            public int Cursor { get; }

            /// <summary>The units an earlier page left open, in document order.</summary>
            public OpenUnits Open { get; }

            /// <summary>The end of the page's text the state is searched to.</summary>
            public int End { get; }

            /// <summary>The unit whose consumed count the entry wrote, or -1 when it wrote none.</summary>
            public int UndoUnit { get; }

            /// <summary>Which family of alternatives the state is trying.</summary>
            public int Phase { get; set; }

            /// <summary>The unit the current family belongs to.</summary>
            public int Unit { get; set; }

            /// <summary>How much of the unit was consumed before this state.</summary>
            public int Offset { get; set; }

            /// <summary>The text of the unit, normalised.</summary>
            public string Remaining { get; set; } = string.Empty;

            /// <summary>The next length of the current family to try.</summary>
            public int NextLength { get; set; }

            /// <summary>The length the current alternative tries.</summary>
            public int CandidateLength { get; set; }
        }
    }
}
