using System.Text;
using QRCoder;
using ReportService.Pdf.Verification.PageCodes;

namespace ReportService.Pdf.PageCodes.QrCoder;

/// <summary>
/// The example QR generator (<c>qrcoder-qr</c>). Option <c>errorCorrection</c>:
/// <c>L</c>, <c>M</c> (default), <c>Q</c> or <c>H</c>. The symbol version is fixed per
/// declaration - the smallest that holds the declaration's largest value at its error
/// correction - so every page's code has the same module count and its size is known
/// at take. The area must be square.
/// </summary>
public sealed class QrCoderPageCodeGenerator : IPageCodeGenerator
{
    /// <summary>The name a template declares.</summary>
    public const string GeneratorName = "qrcoder-qr";

    private const string ErrorCorrectionOption = "errorCorrection";

    /// <inheritdoc />
    public string Name => GeneratorName;

    /// <inheritdoc />
    public PageCodeCheck Validate(IReadOnlyDictionary<string, string> options, PageCodeArea area, int maxValueBytes)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!TryLevel(options, out var level))
        {
            return PageCodeCheck.Refuse(
                "the qrcoder-qr option errorCorrection is not L, M, Q or H, or another option is declared",
                "declare errorCorrection as L, M, Q or H, and no other option");
        }

        if (Math.Abs(area.WidthPt - area.HeightPt) > 0.01)
        {
            return PageCodeCheck.Refuse("the qrcoder-qr example needs a square area", "declare the same width and height");
        }

        if (VersionFor(level, maxValueBytes) is not { } version)
        {
            return PageCodeCheck.Refuse(
                "the declared value can be longer than a QR code holds at this error correction",
                "shorten the value template or lower errorCorrection");
        }

        return PageCodeCheck.Accept(ModuleMatrixContent.WorstCase(Size(version), area));
    }

    /// <inheritdoc />
    public PageCodeContent Generate(string value, IReadOnlyDictionary<string, string> options, PageCodeArea area)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        if (!TryLevel(options, out var level))
        {
            return PageCodeContent.Refused("the qrcoder-qr options are not valid");
        }

        // The version is the one the take fixed for the declaration's largest value.
        var version = VersionFor(level, PageCodeSource.MaximumValueBytes) ?? 40;
        QRCodeData data;
        try
        {
            using var generator = new QRCodeGenerator();
            data = generator.CreateQrCode(value, level, forceUtf8: true, utf8BOM: false, eciMode: QRCodeGenerator.EciMode.Default, requestedVersion: version);
        }
        catch (QRCoder.Exceptions.DataTooLongException)
        {
            return PageCodeContent.Refused("the value does not fit the declaration's QR version");
        }

        using (data)
        {
            var modules = data.ModuleMatrix;
            var size = modules.Count;
            var grid = new bool[size, size];
            for (var row = 0; row < size; row++)
            {
                for (var column = 0; column < size; column++)
                {
                    grid[row, column] = modules[row][column];
                }
            }

            return PageCodeContent.Of(ModuleMatrixContent.Draw(grid, area));
        }
    }

    private static bool TryLevel(IReadOnlyDictionary<string, string> options, out QRCodeGenerator.ECCLevel level)
    {
        level = QRCodeGenerator.ECCLevel.M;
        foreach (var (key, value) in options)
        {
            if (key != ErrorCorrectionOption)
            {
                return false;
            }

            switch (value)
            {
                case "L": level = QRCodeGenerator.ECCLevel.L; break;
                case "M": level = QRCodeGenerator.ECCLevel.M; break;
                case "Q": level = QRCodeGenerator.ECCLevel.Q; break;
                case "H": level = QRCodeGenerator.ECCLevel.H; break;
                default: return false;
            }
        }

        return true;
    }

    // The smallest version that holds a value of maxValueBytes UTF-8 bytes in byte mode.
    private static int? VersionFor(QRCodeGenerator.ECCLevel level, int maxValueBytes)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(
                Encoding.UTF8.GetString(Enumerable.Repeat((byte)'~', Math.Max(1, maxValueBytes)).ToArray()),
                level,
                forceUtf8: true,
                utf8BOM: false,
                eciMode: QRCodeGenerator.EciMode.Default);
            return data.Version;
        }
        catch (QRCoder.Exceptions.DataTooLongException)
        {
            return null;
        }
    }

    // The module matrix QRCoder returns includes a four-module quiet zone on each side.
    private static int Size(int version) => 21 + (4 * (version - 1)) + 8;
}
