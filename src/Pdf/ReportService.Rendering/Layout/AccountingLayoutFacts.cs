using System.Text.Json;
using ReportService.Templates;

namespace ReportService.Rendering.Layout;

/// <summary>
/// The facts one accounting print is given, all of them read from the block's index
/// page and the taken manifest: the block's opening carried-forward, the open group's
/// key and accumulated amount, the reserved band and its three labels, the scale and
/// culture the values are written in, and the content area the rows are measured at.
/// The values become document attributes and are never a script: the scripts the print
/// serves are fixed, hash-pinned inline text.
/// </summary>
/// <param name="OpeningCarriedForward">The running sum of every earlier block's amounts.</param>
/// <param name="OpenGroupKey">The group key the block opens with when it continues a group.</param>
/// <param name="OpeningGroupAccumulated">The open group's sum over the earlier blocks.</param>
/// <param name="Footer">The reserved band and the template's labels, in millimetres.</param>
/// <param name="Scale">The fraction digits the values are written with.</param>
/// <param name="Culture">The BCP 47 culture the values are written in.</param>
/// <param name="ContentWidthMm">The width of the content area, in millimetres.</param>
/// <param name="ContentHeightMm">The height of the content area, in millimetres.</param>
public sealed record AccountingLayoutFacts(
    decimal OpeningCarriedForward,
    string? OpenGroupKey,
    decimal? OpeningGroupAccumulated,
    AccountingFooter Footer,
    int Scale,
    string Culture,
    double ContentWidthMm,
    double ContentHeightMm)
{
    /// <summary>The attribute that opens the accounting facts the layout script reads.</summary>
    public const string Attribute = "data-accounting";

    /// <summary>
    /// The attributes the marking writes on the document root: the named opening values
    /// and the one JSON payload the layout script reads.
    /// </summary>
    public string Attributes()
    {
        var payload = JsonSerializer.Serialize(new
        {
            scale = Scale,
            culture = Culture,
            decimalSeparator = CultureInfoOrInvariant(Culture).NumberFormat.NumberDecimalSeparator,
            contentWidthMm = ContentWidthMm,
            contentHeightMm = ContentHeightMm,
            footer = new
            {
                leftMm = Footer.LeftMm,
                topMm = Footer.TopMm,
                widthMm = Footer.WidthMm,
                heightMm = Footer.HeightMm,
            },
            labels = new
            {
                broughtForward = Footer.BroughtForward,
                pageSubtotal = Footer.PageSubtotal,
                carriedForward = Footer.CarriedForward,
            },
        });

        var text = " data-brought-forward=\"" + OpeningCarriedForward.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"";
        if (!string.IsNullOrEmpty(OpenGroupKey))
        {
            text += " data-open-group=\"" + System.Net.WebUtility.HtmlEncode(OpenGroupKey) + "\"";
        }

        if (OpeningGroupAccumulated is { } accumulated)
        {
            text += " data-group-opened-with=\"" + accumulated.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"";
        }

        return text + " " + Attribute + "=\"" + System.Net.WebUtility.HtmlEncode(payload) + "\"";
    }

    private static System.Globalization.CultureInfo CultureInfoOrInvariant(string? name)
    {
        try
        {
            return string.IsNullOrWhiteSpace(name)
                ? System.Globalization.CultureInfo.InvariantCulture
                : System.Globalization.CultureInfo.GetCultureInfo(name);
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return System.Globalization.CultureInfo.InvariantCulture;
        }
    }
}
