using System.Text.Json;
using ReportService.Pdf.Verification;
using ReportService.Templates;

namespace ReportService.PdfStructure.Tests;

/// <summary>
/// A golden's page/frame declaration: the paper, the margins the reader bands the
/// page with, and the columns that say where the row-key column sits. It is the
/// same declaration shape the cycle's oracle reads, so a golden stays consistent
/// with the rendering cycle that produced it.
/// </summary>
/// <param name="Paper">The paper name, for the diagnostics.</param>
/// <param name="Margins">The declared margins, in millimetres.</param>
/// <param name="RowKey">The column whose cell carries the row's key.</param>
/// <param name="Columns">The columns and their horizontal spans, in millimetres.</param>
internal sealed record GoldenExpectation(
    string Paper,
    PageMarginsMm Margins,
    string RowKey,
    IReadOnlyList<GoldenColumn> Columns)
{
    /// <summary>Reads a declaration from its file.</summary>
    public static GoldenExpectation Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var margin = root.GetProperty("marginMm");
        var columns = root.GetProperty("columns").EnumerateArray()
            .Select(column => new GoldenColumn(
                column.GetProperty("column").GetString() ?? string.Empty,
                column.GetProperty("startMm").GetDouble(),
                column.GetProperty("endMm").GetDouble()))
            .ToList();

        return new GoldenExpectation(
            root.GetProperty("paper").GetString() ?? string.Empty,
            new PageMarginsMm(
                margin.GetProperty("top").GetDouble(),
                margin.GetProperty("right").GetDouble(),
                margin.GetProperty("bottom").GetDouble(),
                margin.GetProperty("left").GetDouble()),
            root.GetProperty("rowKey").GetString() ?? string.Empty,
            columns);
    }

    /// <summary>The horizontal span of the row-key column, in points.</summary>
    public (double StartPt, double EndPt) RowKeyColumnPoints()
    {
        var column = Columns.SingleOrDefault(candidate => string.Equals(candidate.Column, RowKey, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"The declaration names row key '{RowKey}', which is not one of its columns.");
        return (column.StartMm * PlacementConvention.PointsPerMillimetre, column.EndMm * PlacementConvention.PointsPerMillimetre);
    }
}

/// <summary>One column of the printed table and its horizontal span, in millimetres.</summary>
/// <param name="Column">The column's name.</param>
/// <param name="StartMm">Its left edge, from the page's left edge.</param>
/// <param name="EndMm">Its right edge, from the page's left edge.</param>
internal sealed record GoldenColumn(string Column, double StartMm, double EndMm);
