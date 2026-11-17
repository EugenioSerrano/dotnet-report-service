using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReportService.TemplatePackages.Manifest;

namespace ReportService.Rendering;

/// <summary>
/// The one rule the service writes for a page: the paper size and the margins the
/// template declared, in millimetres, with the digits it stored and in the invariant
/// culture, marked important so a package's own page rule cannot override it. The
/// rule is appended after the composed document and is the only inline style the
/// content security policy allows, by the hash of its own bytes.
/// </summary>
/// <param name="Css">The rule's exact text.</param>
/// <param name="CspHashSource">The content security policy's hash source for exactly that text.</param>
public sealed record PageRule(string Css, string CspHashSource)
{
    /// <summary>The page rule of <paramref name="settings"/>.</summary>
    /// <param name="settings">The page the template declared.</param>
    public static PageRule From(PdfPageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var css =
            "@page { size: " + Number(settings.WidthMm) + "mm " + Number(settings.HeightMm)
            + "mm !important; margin: " + Number(settings.MarginTopMm) + "mm " + Number(settings.MarginRightMm)
            + "mm " + Number(settings.MarginBottomMm) + "mm " + Number(settings.MarginLeftMm) + "mm !important; }";

        return new PageRule(css, "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(css))) + "'");
    }

    /// <summary>The document as it is served: <paramref name="document"/> with this rule as its last element.</summary>
    /// <param name="document">The composed document.</param>
    public string AppendTo(string document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document + "<style>" + Css + "</style>";
    }

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
