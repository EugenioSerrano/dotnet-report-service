using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReportService.PdfOracle;

/// <summary>One column of the printed detail table, in millimetres from the page's left edge.</summary>
/// <param name="Column">The name the judge reads the column by.</param>
/// <param name="StartMm">The millimetre where the column starts.</param>
/// <param name="EndMm">The millimetre where the column ends.</param>
internal sealed record ColumnBand(string Column, double StartMm, double EndMm);

/// <summary>
/// What the harness declares about the package whose printed documents this judge
/// reads: the page it lays out, the margins, the columns of the detail table, the
/// words of its column captions, the page framing and the header fields printed
/// before and after the rows. The declaration is written beside the packages and
/// a case holds it to the words the package's own manifest declares, so the two
/// cannot drift apart unnoticed.
/// </summary>
internal sealed class ExpectationDocument
{
    private ExpectationDocument(
        string paper,
        double widthMm,
        double heightMm,
        double topMm,
        double rightMm,
        double bottomMm,
        double leftMm,
        IReadOnlyList<ColumnBand> columns,
        string rowKey,
        string amountColumn,
        IReadOnlyList<string> headRow,
        IReadOnlyList<string> headerAnchors,
        string? trail,
        bool grouped,
        bool blockTotal)
    {
        Paper = paper;
        WidthMm = widthMm;
        HeightMm = heightMm;
        TopMm = topMm;
        RightMm = rightMm;
        BottomMm = bottomMm;
        LeftMm = leftMm;
        Columns = columns;
        RowKey = rowKey;
        AmountColumn = amountColumn;
        HeadRow = headRow;
        HeaderAnchors = headerAnchors;
        Trail = trail;
        Grouped = grouped;
        BlockTotal = blockTotal;
    }

    /// <summary>The paper the package declares, as its manifest names it.</summary>
    public string Paper { get; }

    /// <summary>The page's width in millimetres.</summary>
    public double WidthMm { get; }

    /// <summary>The page's height in millimetres.</summary>
    public double HeightMm { get; }

    /// <summary>The top margin in millimetres.</summary>
    public double TopMm { get; }

    /// <summary>The right margin in millimetres.</summary>
    public double RightMm { get; }

    /// <summary>The bottom margin in millimetres.</summary>
    public double BottomMm { get; }

    /// <summary>The left margin in millimetres.</summary>
    public double LeftMm { get; }

    /// <summary>The columns of the detail table, left to right.</summary>
    public IReadOnlyList<ColumnBand> Columns { get; }

    /// <summary>
    /// The column whose value identifies a record across the pages of one row: a line
    /// that begins a row carries a record number there. It is a declared column.
    /// </summary>
    public string RowKey { get; }

    /// <summary>
    /// The column whose value must print on the page the row begins on (and that a
    /// trailer may carry). It is a declared column.
    /// </summary>
    public string AmountColumn { get; }

    /// <summary>The words of the column captions, in the order the package prints them.</summary>
    public IReadOnlyList<string> HeadRow { get; }

    /// <summary>The header members the package prints before the rows, as member paths.</summary>
    public IReadOnlyList<string> HeaderAnchors { get; }

    /// <summary>The header member the package prints after the rows, or <see langword="null"/> when it prints none.</summary>
    public string? Trail { get; }

    /// <summary>Whether the package begins groups among its records.</summary>
    public bool Grouped { get; }

    /// <summary>Whether the package prints a block total.</summary>
    public bool BlockTotal { get; }

    /// <summary>The content area's top edge in points from the page's top.</summary>
    public double ContentTopPt => Points.Of(TopMm);

    /// <summary>The content area's bottom edge in points from the page's top.</summary>
    public double ContentBottomPt => Points.Of(HeightMm - BottomMm);

    /// <summary>The page's width in points.</summary>
    public double WidthPt => Points.Of(WidthMm);

    /// <summary>The page's height in points.</summary>
    public double HeightPt => Points.Of(HeightMm);

    /// <summary>Reads the declaration written at <paramref name="path"/>.</summary>
    public static ExpectationDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Parse(File.ReadAllText(path));
    }

    /// <summary>Reads a declaration from its text.</summary>
    public static ExpectationDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var root = JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidDataException("The declaration is not a JSON object.");
        var margins = Member(root, "marginMm") as JsonObject
            ?? throw new InvalidDataException("The declaration holds no margins.");
        var columns = Member(root, "columns") as JsonArray
            ?? throw new InvalidDataException("The declaration holds no columns.");
        var headRow = Member(root, "headRow") as JsonArray
            ?? throw new InvalidDataException("The declaration holds no column captions.");
        var anchors = Member(root, "headerAnchors") as JsonArray
            ?? throw new InvalidDataException("The declaration holds no header members.");
        if (columns.Count == 0)
        {
            throw new InvalidDataException("The declaration holds no column.");
        }

        if (headRow.Count == 0)
        {
            throw new InvalidDataException("The declaration holds no column captions.");
        }

        if (anchors.Count == 0)
        {
            throw new InvalidDataException("The declaration holds no header member.");
        }

        return new ExpectationDocument(
            Text(root, "paper"),
            Number(root, "widthMm"),
            Number(root, "heightMm"),
            Number(margins, "top"),
            Number(margins, "right"),
            Number(margins, "bottom"),
            Number(margins, "left"),
            [.. columns.Select(column => column as JsonObject
                ?? throw new InvalidDataException("A column is not a JSON object."))
                .Select(column => new ColumnBand(Text(column, "column"), Number(column, "startMm"), Number(column, "endMm")))],
            Role(root, "rowKey", columns),
            Role(root, "amountColumn", columns),
            [.. headRow.Select(word => word?.GetValue<string>()
                ?? throw new InvalidDataException("A column caption is not text."))],
            [.. anchors.Select(anchor => anchor?.GetValue<string>()
                ?? throw new InvalidDataException("A header member is not a member path."))],
            root["trail"] is JsonValue trail && trail.TryGetValue<string>(out var trailPath) ? trailPath : null,
            Flag(root, "grouped"),
            Flag(root, "blockTotal"));
    }

    // A declared role names one of the declaration's own columns: the judge never guesses
    // which column identifies a record or which one must print on the row's first page.
    private static string Role(JsonObject root, string name, JsonArray columns)
    {
        if (root[name] is not JsonValue value || !value.TryGetValue<string>(out var role) || string.IsNullOrWhiteSpace(role))
        {
            throw new InvalidDataException($"The declaration holds no '{name}'.");
        }

        var declared = columns
            .Select(column => column as JsonObject)
            .Select(column => column is null ? null : column["column"]?.GetValue<string>())
            .Where(column => column is not null)
            .ToList();
        return declared.Contains(role, StringComparer.Ordinal)
            ? role
            : throw new InvalidDataException($"The declaration's '{name}' names no column it declares.");
    }

    private static JsonNode Member(JsonObject node, string name) =>
        node[name] ?? throw new InvalidDataException($"The declaration holds no '{name}'.");

    private static string Text(JsonObject node, string name) =>
        Member(node, name).GetValue<string>();

    private static double Number(JsonObject node, string name) =>
        Member(node, name).GetValue<double>();

    private static bool Flag(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>Converts millimetres to the points a printed page is measured in.</summary>
    internal static class Points
    {
        /// <summary>How many points one millimetre is.</summary>
        public const double PerMm = 72.0 / 25.4;

        /// <summary>The length of <paramref name="millimetres"/> in points.</summary>
        public static double Of(double millimetres) => millimetres * PerMm;

        /// <summary>The number of points, written the way a decimal is written in every culture.</summary>
        public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
