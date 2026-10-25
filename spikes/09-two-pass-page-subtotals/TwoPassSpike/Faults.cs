namespace TwoPassSpike;

/// <summary>
/// The faults of the accounting checker's controls. A fault changes only what its
/// description says, in the second pass, on a copy of the computed values or of
/// the reported read-back; the fixture file is never touched. Each fault is
/// paired with the base fixture it runs on (table markup, strategy "stamped").
/// </summary>
internal static class Faults
{
    /// <summary>The row the row-level faults act on (the tall row of the row fixtures).</summary>
    public const int TallRow = 50;

    /// <summary>AC-7: side of the extra square, and its centre on page 1 — in the left margin at the page's mid-height, where nothing is printed.</summary>
    public const double ExtraSquareSideMm = 1.0;
    public const double ExtraSquareXMm = 6.0;
    public const double ExtraSquareYMm = 148.5;

    /// <summary>AC-8: side of the square drawn over row 1's sequence number.</summary>
    public const double InkSquareSideMm = 0.5;

    public static IReadOnlyList<(string Id, string Fixture)> All { get; } =
    [
        ("AC-1", "B100"), ("AC-2", "B100"), ("AC-3", "T300u"), ("AC-4", "T300u"), ("AC-5", "T300u"), ("AC-6", "T300u"),
        ("AC-7", "B100"), ("AC-8", "B100"), ("RB-1", "T300u"), ("RB-2", "B100"), ("RB-3", "B100"),
    ];

    public static string Describe(string fault) => fault switch
    {
        "AC-1" => "page 2's printed subtotal + 0.01; the chain carried from the printed values",
        "AC-2" => "page 3's brought forward printed as carried(2) + 0.01, its carried = brought + subtotal",
        "AC-3" => "row 50 counted on its start page (subtotals only; its amount stamp unchanged)",
        "AC-4" => "no \"(continues)\" on row 50's second page",
        "AC-5" => "an extra \"(continues)\" on row 50's last page",
        "AC-6" => "row 50's amount stamped on its start line (start page) instead of its last line",
        "AC-7" => "one extra 1 mm filled black square on page 1, in the left margin at mid-height (6 mm, 148.5 mm)",
        "AC-8" => "a 0.5 mm filled square on row 1's start-marker rectangle (over the printed sequence number)",
        "RB-1" => "the reported read-back gives row 50's end page + 1 (stamping uses the true map)",
        "RB-2" => "row 51 omitted from the reported read-back",
        "RB-3" => "row 11's reported start rectangle grown upwards to 1 pt above row 10's (it still holds row 11's sequence number), so the two rows sort in swapped order",
        _ => throw new ArgumentException($"Unknown fault '{fault}'.", nameof(fault)),
    };

    /// <summary>Changes the read-back as reported (written to readback/&lt;run&gt;.json); the stamping keeps the true map.</summary>
    public static ReadBack Reported(ReadBack readBack, string? fault)
    {
        if (fault is not ("RB-1" or "RB-2" or "RB-3"))
        {
            return readBack;
        }

        var copy = new ReadBack
        {
            AnnotationsRead = readBack.AnnotationsRead,
            MarkerAnnotations = readBack.MarkerAnnotations,
            UnknownAnnotations = readBack.UnknownAnnotations,
            PagesRead = readBack.PagesRead,
            Unknown = [.. readBack.Unknown],
            Rows = [.. readBack.Rows.Select(Clone)],
        };
        switch (fault)
        {
            case "RB-1":
                {
                    var row = copy.Rows.Single(r => r.Seq == TallRow);
                    row.EndPage++;
                    break;
                }

            case "RB-2":
                copy.Rows.RemoveAll(r => r.Seq == TallRow + 1);
                break;
            case "RB-3":
                {
                    var ten = copy.Rows.Single(r => r.Seq == 10);
                    var eleven = copy.Rows.Single(r => r.Seq == 11);
                    eleven.StartRect = eleven.StartRect! with { Y0 = ten.StartRect!.Y0 - 1.0 };
                    break;
                }
        }

        return copy;
    }

    /// <summary>Changes the computed plan for the accounting faults (AC-1 … AC-8).</summary>
    public static void Apply(StampPlan plan, ReadBack readBack, string? fault)
    {
        switch (fault)
        {
            case "AC-1":
                plan.Pages[1] = plan.Pages[1] with { Subtotal = plan.Pages[1].Subtotal + 0.01m };
                PageAccounting.Rechain(plan, 2);
                break;
            case "AC-2":
                plan.Pages[2] = plan.Pages[2] with { Brought = plan.Pages[1].Carried + 0.01m };
                plan.Pages[2] = plan.Pages[2] with { Carried = plan.Pages[2].Brought + plan.Pages[2].Subtotal };
                for (var i = 3; i < plan.Pages.Count; i++)
                {
                    plan.Pages[i] = plan.Pages[i] with { Brought = plan.Pages[i - 1].Carried, Carried = plan.Pages[i - 1].Carried + plan.Pages[i].Subtotal };
                }

                break;
            case "AC-3":
                {
                    var row = readBack.Rows.Single(r => r.Seq == TallRow);
                    var amount = PageAccounting.Amount(plan.Amounts.Count > 0 ? plan.Amounts.Single(a => a.Seq == TallRow).Text : throw new InvalidOperationException("AC-3 needs strategy stamped."));
                    plan.Pages[row.StartPage - 1] = plan.Pages[row.StartPage - 1] with { Subtotal = plan.Pages[row.StartPage - 1].Subtotal + amount };
                    plan.Pages[row.EndPage - 1] = plan.Pages[row.EndPage - 1] with { Subtotal = plan.Pages[row.EndPage - 1].Subtotal - amount };
                    PageAccounting.Rechain(plan, row.StartPage);
                    break;
                }

            case "AC-4":
                {
                    var row = readBack.Rows.Single(r => r.Seq == TallRow);
                    plan.Pages[row.StartPage] = plan.Pages[row.StartPage] with { Continues = false };
                    break;
                }

            case "AC-5":
                {
                    var row = readBack.Rows.Single(r => r.Seq == TallRow);
                    plan.Pages[row.EndPage - 1] = plan.Pages[row.EndPage - 1] with { Continues = true };
                    break;
                }

            case "AC-6":
                {
                    var row = readBack.Rows.Single(r => r.Seq == TallRow);
                    var index = plan.Amounts.FindIndex(a => a.Seq == TallRow);
                    plan.Amounts[index] = plan.Amounts[index] with { Page = row.StartPage, Line = row.StartRect! };
                    break;
                }

            case "AC-7":
                plan.Squares.Add((1, Stamper.MmToPt(ExtraSquareXMm), Stamper.MmToPt(ExtraSquareYMm), Stamper.MmToPt(ExtraSquareSideMm)));
                break;
            case "AC-8":
                {
                    var row = readBack.Rows.Single(r => r.Seq == readBack.Rows.Min(x => x.Seq));
                    var rect = row.StartRect!;
                    plan.Squares.Add((row.StartPage, (rect.X0 + rect.X1) / 2, rect.YCentre, Stamper.MmToPt(InkSquareSideMm)));
                    break;
                }

            default:
                break;
        }
    }

    private static ReadBackRow Clone(ReadBackRow r) => new()
    {
        Seq = r.Seq,
        StartPage = r.StartPage,
        StartRect = r.StartRect,
        EndPage = r.EndPage,
        EndRect = r.EndRect,
        EndRectPage = r.EndRectPage,
        AmountMarkerPage = r.AmountMarkerPage,
        StartMarkers = r.StartMarkers,
        EndMarkers = r.EndMarkers,
        AmountMarkers = r.AmountMarkers,
    };
}
