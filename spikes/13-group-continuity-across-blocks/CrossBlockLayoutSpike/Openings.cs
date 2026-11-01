using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PreSplitLayoutSpike;

/// <summary>
/// The opening brought-forward amounts of the parts (handover.json in the fixture
/// folder, written by the fixture builder from the source data and pinned by the
/// folder's SHA256SUMS), and how one reaches the page: as a data-brought-forward
/// attribute on the PDF page's root element, which this folder's accounting script
/// reads as the start of its chain. The page body is not changed.
/// </summary>
internal static partial class Openings
{
    public const string FileName = "handover.json";

    private const string RootTag = "<html lang=\"en\">";

    public static Dictionary<string, string> Read(string fixturesDirectory)
    {
        var path = Path.Combine(fixturesDirectory, FileName);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (fixture, value) in JsonNode.Parse(File.ReadAllText(path))!["openings"]!.AsObject())
        {
            var text = value!.GetValue<string>();
            if (!Amount().IsMatch(text))
            {
                throw new InvalidDataException($"{FileName}: '{text}' for {fixture} is not an amount with two decimals.");
            }

            result[fixture] = text;
        }

        return result;
    }

    /// <summary>The PDF page with the opening amount on its root element; exactly one root tag must exist.</summary>
    public static string Apply(string pdfPage, string opening)
    {
        var at = pdfPage.IndexOf(RootTag, StringComparison.Ordinal);
        if (at < 0 || pdfPage.IndexOf(RootTag, at + 1, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidDataException("The PDF page does not hold exactly one root element tag.");
        }

        var attribute = string.Create(CultureInfo.InvariantCulture, $"<html lang=\"en\" data-brought-forward=\"{opening}\">");
        return string.Concat(pdfPage.AsSpan(0, at), attribute, pdfPage.AsSpan(at + RootTag.Length));
    }

    [GeneratedRegex(@"^-?\d+\.\d{2}$")]
    private static partial Regex Amount();
}
