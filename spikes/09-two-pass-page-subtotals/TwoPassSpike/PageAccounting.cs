using System.Globalization;
using PaginationFixtures;

namespace TwoPassSpike;

/// <summary>What the second pass prints on one page.</summary>
internal sealed record PageValues(int Page, decimal Brought, decimal Subtotal, decimal Carried, bool Continues);

/// <summary>Where the second pass prints one row's amount (strategy "stamped").</summary>
internal sealed record AmountStamp(int Seq, int Page, Rect Line, string Text);

/// <summary>The values the second pass prints: per page, and per row in strategy "stamped".</summary>
internal sealed class StampPlan
{
    public List<PageValues> Pages { get; set; } = [];

    public List<AmountStamp> Amounts { get; set; } = [];

    /// <summary>Extra filled squares of the controls AC-7 and AC-8 (page, centre in points, side in points).</summary>
    public List<(int Page, double X, double Y, double Side)> Squares { get; set; } = [];
}

/// <summary>
/// The computation of the second pass, from the block's source data and the row
/// map read from the markers: amounts are the fixture's decimal text parsed as
/// decimal, never binary floating point. End-page rule: a row counts once, on the
/// page where it ends. Chain: brought(1) = 0, carried(p) = brought(p) + subtotal(p),
/// brought(p + 1) = carried(p). Span rule: a row that starts before the page where
/// it ends continues on every page from its start page to its end page − 1,
/// including pages on which it prints nothing.
/// </summary>
internal static class PageAccounting
{
    public static decimal Amount(string text) =>
        decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    public static string Format(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Computes the plan; returns an error (the pass then ends "guard-failed") when the chain does not end at the block total.</summary>
    public static (StampPlan? Plan, string? Error) Compute(ReadBack readBack, Fixture fixture, string strategy)
    {
        var amounts = fixture.Items.ToDictionary(r => r.Sequence!.Value, r => (Value: Amount(r.Amount!), Text: r.Amount!));
        var pages = readBack.PagesRead;
        var plan = new StampPlan();
        var brought = 0m;
        for (var page = 1; page <= pages; page++)
        {
            var subtotal = readBack.Rows.Where(r => r.EndPage == page).Sum(r => amounts[r.Seq].Value);
            var carried = brought + subtotal;
            var continues = readBack.Rows.Any(r => r.StartPage <= page && r.EndPage > page);
            plan.Pages.Add(new PageValues(page, brought, subtotal, carried, continues));
            brought = carried;
        }

        var total = Amount(fixture.BlockTotal);
        var sum = amounts.Values.Sum(a => a.Value);
        if (brought != sum || sum != total)
        {
            return (null, $"the chain ends at {Format(brought)}, the amounts sum to {Format(sum)}, the block total is {Format(total)}");
        }

        if (strategy == HtmlBuilder.Stamped)
        {
            foreach (var row in readBack.Rows)
            {
                plan.Amounts.Add(new AmountStamp(row.Seq, row.EndRectPage, row.EndRect!, amounts[row.Seq].Text));
            }
        }

        return (plan, null);
    }

    /// <summary>Recomputes the chain from the printed subtotals (used by faults that change a printed value).</summary>
    public static void Rechain(StampPlan plan, int fromPage)
    {
        for (var i = fromPage - 1; i < plan.Pages.Count; i++)
        {
            var p = plan.Pages[i];
            var brought = i == 0 ? 0m : plan.Pages[i - 1].Carried;
            plan.Pages[i] = p with { Brought = brought, Carried = brought + p.Subtotal };
        }
    }
}
