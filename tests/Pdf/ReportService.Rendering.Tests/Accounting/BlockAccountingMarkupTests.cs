using System.Text;
using ReportService.Pdf.Verification;
using ReportService.Rendering.Accounting;
using ReportService.Rendering.Layout;
using ReportService.Templates;

namespace ReportService.Rendering.Tests.Accounting;

/// <summary>
/// The marking of the composed block: the facts on the document element, one row's
/// ordinal, amount and group on its own root, the group's key and finality on its total
/// cell, and the exact invariant amount text. Every mark is an insertion into an existing
/// open tag, so the composed text outside the attributes is unchanged and the text checks
/// read the same units; a record whose amount is absent refuses the block, and an ordinal
/// that repeats is impossible.
/// </summary>
public sealed class BlockAccountingMarkupTests
{
    private static readonly object Missing = new();
    private static readonly AccountingFooter Footer = new(
        12, 282, 186, 12,
        "Saldo anterior", "Subtotal de pagina", "Saldo siguiente");

    [Fact]
    public void TheFactsAreCarriedOnTheDocumentElementAndTheRows()
    {
        var marked = Mark(Block(), ContinuesNextGroup: false);

        Assert.True(marked.Passed, marked.Reason);
        var text = marked.Block!.Text;
        Assert.Contains("data-brought-forward=\"7.50\"", text, StringComparison.Ordinal);
        Assert.Contains("data-open-group=\"A\"", text, StringComparison.Ordinal);
        Assert.Contains("data-group-opened-with=\"3.50\"", text, StringComparison.Ordinal);
        Assert.Contains("data-accounting=", text, StringComparison.Ordinal);
        Assert.Contains("data-seq=\"1\"", text, StringComparison.Ordinal);
        Assert.Contains("data-amount=\"10.00\"", text, StringComparison.Ordinal);
        Assert.Contains("data-group=\"A\"", text, StringComparison.Ordinal);
        Assert.Contains("data-role=\"group-total\" data-group=\"A\" data-group-final=\"true\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AContinuingGroupWritesNoFinality()
    {
        var marked = Mark(Block(), ContinuesNextGroup: true);

        Assert.True(marked.Passed, marked.Reason);
        var text = marked.Block!.Text;
        Assert.Contains("data-role=\"group-total\" data-group=\"A\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("data-group-final", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheComposedTextOutsideTheInsertedAttributesIsUnchanged()
    {
        var original = Block();
        var marked = Mark(original, ContinuesNextGroup: false);

        foreach (var fragment in original.Fragments)
        {
            var before = original.TextOf(fragment);
            var after = marked.Block!.TextOf(marked.Block.Fragments[original.Fragments.ToList().IndexOf(fragment)]);
            Assert.Equal(StripMarks(before), StripMarks(after));
        }

        Assert.Equal(marked.Block!.Text, string.Concat(marked.Block.Fragments.Select(marked.Block.TextOf)));
    }

    [Fact]
    public void ARecordWhoseAmountIsMissingRefusesTheBlock()
    {
        var marked = Mark(Block(), ContinuesNextGroup: false, amount: Missing);

        Assert.False(marked.Passed);
        Assert.Equal(VerificationReasons.AccountingAmountInvalid, marked.Reason);
        Assert.Equal(1, marked.RecordOrdinal);
    }

    [Fact]
    public void ARecordWhoseAmountIsNotANumberRefusesTheBlock()
    {
        var marked = Mark(Block(), ContinuesNextGroup: false, amount: true);

        Assert.False(marked.Passed);
        Assert.Equal(VerificationReasons.AccountingAmountInvalid, marked.Reason);
    }

    [Fact]
    public void TheAmountIsWrittenAtTheDeclaredScaleInInvariantText()
    {
        var marked = Mark(Block(), ContinuesNextGroup: false, amount: 10.005m, scale: 2);

        Assert.True(marked.Passed, marked.Reason);
        Assert.Contains("data-amount=\"10.01\"", marked.Block!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedOrdinalIsImpossible()
    {
        var composition = Block();
        var repeated = new ComposedBlock(
            composition.Html,
            [
                composition.Fragments[0],
                composition.Fragments[1],
                composition.Fragments[1],
                composition.Fragments[2],
            ]);

        var marked = Mark(repeated, ContinuesNextGroup: false);

        Assert.False(marked.Passed);
        Assert.Equal(1, marked.RecordOrdinal);
    }

    private static AccountingMarkupOutcome Mark(ComposedBlock block, bool ContinuesNextGroup, object? amount = null, int scale = 2) =>
        BlockAccountingMarkup.Mark(block, new AccountingMarkupContext(
            "amount",
            scale,
            "es-AR",
            new AccountingLayoutFacts(7.50m, "A", 3.50m, Footer, scale, "es-AR", 186, 264),
            ContinuesNextGroup,
            new Dictionary<long, IReadOnlyDictionary<string, object?>>
            {
                [1] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["amount"] = ReferenceEquals(amount, Missing) ? null : amount is null ? "10.00" : amount,
                    ["group"] = "A",
                },
            }));

    private static ComposedBlock Block()
    {
        var fragments = new (FragmentKind Kind, long Ordinal, string? Group, string Html)[]
        {
            (FragmentKind.DocumentStart, 0, null, "<div class=\"doc\">"),
            (FragmentKind.Row, 1, "A", "<div class=\"item\"><div class=\"desc\">First</div><div class=\"amount\" data-role=\"amount\">10.00</div></div>"),
            (FragmentKind.GroupEnd, 0, "A", "<div class=\"group-total\"><span data-role=\"group-total\"></span></div>"),
            (FragmentKind.DocumentEnd, 0, null, "</div>"),
        };

        var html = new StringBuilder();
        var mapped = new List<ComposedFragment>(fragments.Length);
        foreach (var (kind, ordinal, group, fragmentHtml) in fragments)
        {
            var start = Encoding.UTF8.GetByteCount(html.ToString());
            _ = html.Append(fragmentHtml);
            mapped.Add(new ComposedFragment(kind, start, Encoding.UTF8.GetByteCount(fragmentHtml), ordinal, group));
        }

        return new ComposedBlock(Encoding.UTF8.GetBytes(html.ToString()), mapped);
    }

    private static string StripMarks(string html)
    {
        var builder = new StringBuilder(html.Length);
        var position = 0;
        while (position < html.Length)
        {
            var attribute = html.IndexOf(" data-", position, StringComparison.Ordinal);
            if (attribute < 0)
            {
                _ = builder.Append(html, position, html.Length - position);
                break;
            }

            _ = builder.Append(html, position, attribute - position);
            var end = html.IndexOf('"', attribute + 6);
            var close = end < 0 ? -1 : html.IndexOf('"', end + 1);
            if (close < 0)
            {
                _ = builder.Append(html, attribute, html.Length - attribute);
                break;
            }

            position = close + 1;
        }

        return builder.ToString();
    }
}
