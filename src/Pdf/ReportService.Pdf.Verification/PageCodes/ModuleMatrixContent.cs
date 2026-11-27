using System.Globalization;
using System.Text;

namespace ReportService.Pdf.Verification.PageCodes;

/// <summary>
/// Draws a square grid of dark and light modules - the shape of most two-dimensional
/// codes - as page-code content: one scale onto the area, then one filled rectangle
/// per horizontal run of dark modules in whole modules, origin at the area's
/// lower-left corner. A generator that computes a module matrix with its own library
/// can return this content.
/// </summary>
public static class ModuleMatrixContent
{
    /// <summary>Draws a matrix.</summary>
    /// <param name="dark">The modules, <c>[row, column]</c>, row 0 at the top.</param>
    /// <param name="area">The area's size.</param>
    /// <returns>The content bytes.</returns>
    public static byte[] Draw(bool[,] dark, PageCodeArea area)
    {
        ArgumentNullException.ThrowIfNull(dark);
        var rows = dark.GetLength(0);
        var columns = dark.GetLength(1);

        // One scale maps the module grid onto the area, so every run is written in
        // whole modules: the content stays small whatever the symbol's size.
        var builder = new StringBuilder("q\n")
            .Append(Number(area.WidthPt / columns)).Append(" 0 0 ")
            .Append(Number(area.HeightPt / rows)).Append(" 0 0 cm\n0 g\n");
        for (var row = 0; row < rows; row++)
        {
            var column = 0;
            while (column < columns)
            {
                if (!dark[row, column])
                {
                    column++;
                    continue;
                }

                var start = column;
                while (column < columns && dark[row, column])
                {
                    column++;
                }

                builder.Append(start.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append((rows - 1 - row).ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append((column - start).ToString(CultureInfo.InvariantCulture)).Append(" 1 re\n");
            }
        }

        builder.Append("f\nQ\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    /// <summary>The largest content a <paramref name="size"/> x <paramref name="size"/> matrix can draw: every other module dark.</summary>
    /// <param name="size">The matrix side, in modules.</param>
    /// <param name="area">The area's size.</param>
    /// <returns>The worst-case content bytes.</returns>
    public static byte[] WorstCase(int size, PageCodeArea area)
    {
        var grid = new bool[size, size];
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                grid[row, column] = (row + column) % 2 == 0;
            }
        }

        return Draw(grid, area);
    }

    private static string Number(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);
}
