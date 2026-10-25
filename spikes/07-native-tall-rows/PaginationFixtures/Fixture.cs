namespace PaginationFixtures;

/// <summary>The kind of a row of a fixture's detail, in document order.</summary>
public enum RowKind
{
    GroupStart,
    Item,
    GroupEnd,
}

/// <summary>
/// One row of a fixture's detail. Group-start rows carry the group id and name,
/// item rows the record fields, group-end rows the group id and its total.
/// Amounts and totals are decimal text with two decimals, invariant culture.
/// </summary>
public sealed record FixtureRow
{
    public required RowKind Kind { get; init; }

    public string? GroupId { get; init; }

    public string? GroupName { get; init; }

    public string? Total { get; init; }

    public int? Sequence { get; init; }

    public string? Date { get; init; }

    public string? Description { get; init; }

    /// <summary>Only in five-column fixtures; empty for every row but the one that carries a note.</summary>
    public string? Note { get; init; }

    public string? Amount { get; init; }

    /// <summary>The printed text of a group-start or group-end row.</summary>
    public string CaptionText => Kind switch
    {
        RowKind.GroupStart => $"{GroupId} — {GroupName}",
        RowKind.GroupEnd => $"{Captions.GroupTotal} {Total}",
        _ => throw new InvalidOperationException("An item row has no caption text."),
    };
}

/// <summary>
/// One fixture: a block of the grouped dataset as it is printed — title, free
/// text before, the detail rows, the block-total row and free text after.
/// </summary>
public sealed record Fixture
{
    public const string FourColumn = "four-column";
    public const string FiveColumn = "five-column";
    public const string AlignTop = "top";
    public const string AlignBottom = "bottom";
    public const string WhiteSpaceNormal = "normal";
    public const string WhiteSpacePreLine = "pre-line";

    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Geometry { get; init; }

    public required string AmountAlign { get; init; }

    public required string WhiteSpace { get; init; }

    public required IReadOnlyList<string> FreeTextBefore { get; init; }

    public required IReadOnlyList<string> FreeTextAfter { get; init; }

    public required IReadOnlyList<FixtureRow> Rows { get; init; }

    public required string BlockTotal { get; init; }

    public IEnumerable<FixtureRow> Items => Rows.Where(r => r.Kind == RowKind.Item);

    public bool HasNote => Geometry == FiveColumn;

    /// <summary>The printed text of the block-total row.</summary>
    public string BlockTotalText => $"{Captions.BlockTotal} {BlockTotal}";
}

/// <summary>Fixed caption texts printed in every block; asserted absent from every generated text.</summary>
public static class Captions
{
    public const string TitlePrefix = "Pagination fixture ";
    public const string HeaderFourColumn = "Seq Date Description Amount";
    public const string HeaderFiveColumn = "Seq Date Description Note Amount";
    public const string BlockTotal = "Block total";
    public const string GroupTotal = "Group total";
    public const string ContinuesMark = "(continues)";

    public static string Header(string geometry) => geometry == Fixture.FiveColumn ? HeaderFiveColumn : HeaderFourColumn;

    /// <summary>The page-counter text of page <paramref name="page"/> of <paramref name="pages"/>.</summary>
    public static string PageCounter(int page, int pages) => $"Page {page} of {pages}";
}

/// <summary>
/// The declared page geometry of the template (never read from the engine):
/// A4 portrait, margins 15 / 12 / 18 / 12 mm, column bands in mm from the
/// page's left edge, vertical bands top-down.
/// </summary>
public static class DeclaredGeometry
{
    public const double PageWidthMm = 210;
    public const double PageHeightMm = 297;
    public const double ContentTopMm = 15;
    public const double ContentBottomMm = 279;
    public const double PageWidthPt = 595.28;
    public const double PageHeightPt = 841.89;
    public const double PageSizeTolerancePt = 0.5;

    public static double MmToPt(double mm) => mm * 72.0 / 25.4;

    /// <summary>Column bands [start, end) in mm, in left-to-right order.</summary>
    public static IReadOnlyList<(string Column, double StartMm, double EndMm)> Columns(string geometry) => geometry == Fixture.FiveColumn
        ? [("seq", 12, 26), ("date", 26, 48), ("description", 48, 108), ("note", 108, 168), ("amount", 168, 198)]
        : [("seq", 12, 26), ("date", 26, 48), ("description", 48, 168), ("amount", 168, 198)];
}
