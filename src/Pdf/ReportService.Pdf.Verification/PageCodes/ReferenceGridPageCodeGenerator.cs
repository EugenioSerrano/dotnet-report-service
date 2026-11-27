using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReportService.Pdf.Verification.PageCodes;

/// <summary>
/// The product's reference generator: a 16 x 16 grid whose cells are dark or light
/// by the bits of the value's SHA-256. It uses no library, is deterministic and is
/// distinct per value, and its content has the same length for every value. It is
/// not a scannable symbol: it shows adopters what a generator returns and lets the
/// product's own tests draw and verify codes without a third-party library. It is
/// registered only when the deployment's configuration enables it.
/// </summary>
public sealed class ReferenceGridPageCodeGenerator : IPageCodeGenerator
{
    /// <summary>The name a template declares.</summary>
    public const string GeneratorName = "reference-grid";

    private const int Cells = 16;

    /// <inheritdoc />
    public string Name => GeneratorName;

    /// <inheritdoc />
    public PageCodeCheck Validate(IReadOnlyDictionary<string, string> options, PageCodeArea area, int maxValueBytes)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count != 0)
        {
            return PageCodeCheck.Refuse("the reference-grid generator takes no options", "remove pageCode.options");
        }

        // Every value draws all 256 cells with fixed-width numbers, so any value's
        // content is the worst case.
        return PageCodeCheck.Accept(Draw(new string('x', Math.Max(1, maxValueBytes)), area));
    }

    /// <inheritdoc />
    public PageCodeContent Generate(string value, IReadOnlyDictionary<string, string> options, PageCodeArea area)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        return PageCodeContent.Of(Draw(value, area));
    }

    private static byte[] Draw(string value, PageCodeArea area)
    {
        var bits = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var cellWidth = area.WidthPt / Cells;
        var cellHeight = area.HeightPt / Cells;
        var builder = new StringBuilder("q\n");
        for (var row = 0; row < Cells; row++)
        {
            for (var column = 0; column < Cells; column++)
            {
                var index = (row * Cells) + column;
                var dark = (bits[index / 8] >> (7 - (index % 8)) & 1) == 1;
                builder.Append(dark ? "0 g " : "1 g ")
                    .Append(Number(column * cellWidth)).Append(' ')
                    .Append(Number((Cells - 1 - row) * cellHeight)).Append(' ')
                    .Append(Number(cellWidth)).Append(' ')
                    .Append(Number(cellHeight)).Append(" re f\n");
            }
        }

        builder.Append("Q\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string Number(double value) =>
        value.ToString("0000.0000", CultureInfo.InvariantCulture);
}
