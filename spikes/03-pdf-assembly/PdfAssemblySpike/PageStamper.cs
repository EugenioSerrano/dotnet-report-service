using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfAssemblySpike;

/// <summary>
/// Draws "Page X of Y" centred in the reserved footer band of an existing page,
/// appending to the page's content without touching what is already there.
/// </summary>
internal static class PageStamper
{
    /// <summary>
    /// When true the stamp is wrapped in a saved and restored graphics state.
    /// Off by default; switched on only if the plain append changes pixels
    /// outside the footer band.
    /// </summary>
    public static readonly bool WrapInGraphicsState = false;

    public static string Text(int number, int total) =>
        string.Create(CultureInfo.InvariantCulture, $"Page {number} of {total}");

    public static void Stamp(PdfPage page, int number, int total)
    {
        string text = Text(number, total);
        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        if (WrapInGraphicsState)
        {
            gfx.Save();
        }

        var font = PageLayout.PageNumber;
        double width = gfx.MeasureString(text, font).Width;
        double x = (page.Width.Point - width) / 2;
        double y = page.Height.Point - PageLayout.NumberBaselineFromBottom;
        gfx.DrawString(text, font, XBrushes.Black, x, y);
        if (WrapInGraphicsState)
        {
            gfx.Restore();
        }
    }
}
