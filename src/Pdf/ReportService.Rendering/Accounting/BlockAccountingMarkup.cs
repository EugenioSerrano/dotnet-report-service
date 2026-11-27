using System.Text;
using ReportService.Pdf.Verification;
using ReportService.Rendering.Layout;
using ReportService.Templates;

namespace ReportService.Rendering.Accounting;

/// <summary>What the accounting marking writes into the composed block.</summary>
/// <param name="AmountField">The record field the totals sum.</param>
/// <param name="Scale">The fraction digits the amounts are written with.</param>
/// <param name="Culture">The presentation culture the band's values are written in.</param>
/// <param name="Facts">The block's opening facts, the band and the content area.</param>
/// <param name="ContinuesNextGroup">Whether the block's last group continues into the next block.</param>
/// <param name="Records">The block's records, by ordinal, as the composer read them.</param>
internal sealed record AccountingMarkupContext(
    string AmountField,
    int Scale,
    string Culture,
    AccountingLayoutFacts Facts,
    bool ContinuesNextGroup,
    IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>> Records);

/// <summary>The composed block after marking, or the reason the marking refused it.</summary>
/// <param name="Block">The marked block, or <see langword="null"/> when the marking refused.</param>
/// <param name="Reason">The refusal's reason word, or <see langword="null"/>.</param>
/// <param name="RecordOrdinal">The record the refusal was found at, or <see langword="null"/>.</param>
internal sealed record AccountingMarkupOutcome(ComposedBlock? Block, string? Reason, long? RecordOrdinal = null)
{
    /// <summary>Whether the block was marked.</summary>
    public bool Passed => Block is not null;
}

/// <summary>
/// Marks the composed block for the accounting print, from the block's own records and
/// the partition's facts — never from the template's text. The document element carries
/// the block's opening carried-forward, the open group's key and accumulated amount and
/// the band's facts; every row's one root element carries its ordinal, its amount as
/// exact invariant decimal text at the declared scale and its group; every group-end
/// total cell carries its group and, when the group truly ends in this block, that it is
/// final. Every mark is an insertion into an existing open tag, so the composed text
/// outside the inserted attributes is unchanged and the text checks read the same units.
/// A record whose amount is absent or not a number refuses the block; ordinals are
/// unique by construction, and the marking asserts it.
/// </summary>
internal static class BlockAccountingMarkup
{
    /// <summary>Marks <paramref name="block"/>.</summary>
    /// <param name="block">The composed block and its fragment map.</param>
    /// <param name="context">The amount field, the scale and the block's facts.</param>
    public static AccountingMarkupOutcome Mark(ComposedBlock block, AccountingMarkupContext context)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(context);

        var ordinals = new HashSet<long>();
        var fragments = new List<ComposedFragment>(block.Fragments.Count);
        var output = new List<byte>(block.Html.Length + (block.Fragments.Count * 48));
        var groupEnds = block.Fragments
            .Where(fragment => fragment.Kind == FragmentKind.GroupEnd)
            .ToList();
        var lastGroupEnd = groupEnds.Count > 0 ? groupEnds[^1] : null;

        var rootMarked = false;
        foreach (var fragment in block.Fragments)
        {
            var text = block.TextOf(fragment);
            var insertions = new List<Insertion>();

            if (!rootMarked)
            {
                // The first open tag of the block is the document element the facts live on;
                // a block whose first part emits no element is wrapped so the facts still have
                // one place to live.
                var tag = FirstOpenTag(text, 0);
                if (tag is not null)
                {
                    insertions.Add(new Insertion(tag.Value.BeforeEnd, context.Facts.Attributes()));
                    rootMarked = true;
                }
                else
                {
                    insertions.Add(new Insertion(0, "<div" + context.Facts.Attributes() + ">"));
                    insertions.Add(new Insertion(text.Length, "</div>"));
                    rootMarked = true;
                }
            }

            if (fragment.Kind == FragmentKind.Row)
            {
                if (!ordinals.Add(fragment.RecordOrdinal))
                {
                    return new AccountingMarkupOutcome(null, VerificationReasonDuplicateOrdinal, fragment.RecordOrdinal);
                }

                if (!context.Records.TryGetValue(fragment.RecordOrdinal, out var values))
                {
                    return new AccountingMarkupOutcome(null, VerificationReasons.AccountingAmountInvalid, fragment.RecordOrdinal);
                }

                if (!AccountingAmounts.TryRead(values, context.AmountField, out var amount))
                {
                    return new AccountingMarkupOutcome(null, VerificationReasons.AccountingAmountInvalid, fragment.RecordOrdinal);
                }

                var attributes = " data-seq=\"" + fragment.RecordOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "\" data-amount=\"" + AccountingAmounts.Text(amount, context.Scale) + "\"";
                if (!string.IsNullOrEmpty(fragment.GroupKey))
                {
                    attributes += " data-group=\"" + System.Net.WebUtility.HtmlEncode(fragment.GroupKey) + "\"";
                }

                var tag = FirstOpenTag(text, 0);
                if (tag is null)
                {
                    return new AccountingMarkupOutcome(null, VerificationReasonRowWithoutRoot, fragment.RecordOrdinal);
                }

                insertions.Add(new Insertion(tag.Value.BeforeEnd, attributes));
            }
            else if (fragment.Kind == FragmentKind.GroupEnd && !string.IsNullOrEmpty(fragment.GroupKey))
            {
                var tag = FirstOpenTagCarrying(text, "data-role", "group-total");
                if (tag is null)
                {
                    return new AccountingMarkupOutcome(null, VerificationReasonGroupTotalMissing, fragment.RecordOrdinal);
                }

                var final = !(context.ContinuesNextGroup && ReferenceEquals(fragment, lastGroupEnd));
                insertions.Add(new Insertion(
                    tag.Value.BeforeEnd,
                    " data-group=\"" + System.Net.WebUtility.HtmlEncode(fragment.GroupKey) + "\""
                    + (final ? " data-group-final=\"true\"" : string.Empty)));
            }

            Append(output, text, insertions, out var start, out var length);
            fragments.Add(fragment with { Start = start, Length = length });
        }

        return new AccountingMarkupOutcome(new ComposedBlock([.. output], fragments), null);
    }

    /// <summary>The reason a row carries no unique ordinal the marking can place.</summary>
    public const string VerificationReasonDuplicateOrdinal = "accounting-amount-invalid";

    /// <summary>The reason a row part emits no root element the marking can place.</summary>
    public const string VerificationReasonRowWithoutRoot = "accounting-amount-invalid";

    /// <summary>The reason a group-end part carries no cell the group's total prints in.</summary>
    public const string VerificationReasonGroupTotalMissing = "accounting-group-wrong";

    private readonly record struct Insertion(int At, string Text);

    private static void Append(List<byte> output, string text, List<Insertion> insertions, out int start, out int length)
    {
        start = output.Count;
        var ordered = insertions.Count == 0 ? insertions : [.. insertions.OrderBy(insertion => insertion.At)];
        var position = 0;
        foreach (var insertion in ordered)
        {
            if (insertion.At < position || insertion.At > text.Length)
            {
                continue;
            }

            var prefix = text[position..insertion.At];
            output.AddRange(Encoding.UTF8.GetBytes(prefix));
            output.AddRange(Encoding.UTF8.GetBytes(insertion.Text));
            position = insertion.At;
        }

        output.AddRange(Encoding.UTF8.GetBytes(text[position..]));
        length = output.Count - start;
    }

    /// <summary>The first open tag of <paramref name="text"/> at or after <paramref name="from"/>.</summary>
    private static (int BeforeEnd, string Name)? FirstOpenTag(string text, int from)
    {
        var position = from;
        while (position < text.Length)
        {
            var open = text.IndexOf('<', position);
            if (open < 0)
            {
                return null;
            }

            if (open + 1 >= text.Length || !char.IsLetter(text[open + 1]))
            {
                position = open + 1;
                continue;
            }

            var end = TagEnd(text, open);
            if (end < 0)
            {
                return null;
            }

            var nameEnd = open + 1;
            while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] is not ('>' or '/'))
            {
                nameEnd++;
            }

            var beforeEnd = end > open && text[end - 1] == '/' ? end - 1 : end;
            return (beforeEnd, text[(open + 1)..nameEnd].ToLowerInvariant());
        }

        return null;
    }

    /// <summary>The first open tag whose own attributes carry the attribute and value, literally.</summary>
    private static (int BeforeEnd, string Name)? FirstOpenTagCarrying(string text, string attribute, string value)
    {
        var needles = new[]
        {
            attribute + "=\"" + value + "\"",
            attribute + "='" + value + "'",
        };
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf('<', position);
            if (open < 0)
            {
                return null;
            }

            if (open + 1 >= text.Length || !char.IsLetter(text[open + 1]))
            {
                position = open + 1;
                continue;
            }

            var end = TagEnd(text, open);
            if (end < 0)
            {
                return null;
            }

            var tag = text[open..end];
            foreach (var needle in needles)
            {
                if (tag.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    var nameEnd = open + 1;
                    while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] is not ('>' or '/'))
                    {
                        nameEnd++;
                    }

                    var beforeEnd = text[end - 1] == '/' ? end - 1 : end;
                    return (beforeEnd, text[(open + 1)..nameEnd].ToLowerInvariant());
                }
            }

            position = end;
        }

        return null;
    }

    /// <summary>The position of the '>' that closes the tag opened at <paramref name="open"/>.</summary>
    private static int TagEnd(string text, int open)
    {
        var quote = '\0';
        for (var position = open + 1; position < text.Length; position++)
        {
            var character = text[position];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '>')
            {
                return position;
            }
        }

        return -1;
    }
}
