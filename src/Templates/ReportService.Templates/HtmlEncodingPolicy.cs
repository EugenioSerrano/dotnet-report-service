using System.Net;

namespace ReportService.Templates;

/// <summary>
/// The one escaping policy of every engine: a data value written into HTML is
/// encoded exactly as <see cref="WebUtility.HtmlEncode"/> encodes it — the angle
/// brackets, the ampersand, the two quote characters and the characters
/// U+00A0–U+00FF as numeric references, everything else raw.
/// </summary>
public static class HtmlEncodingPolicy
{
    /// <summary>The policy's encoding of <paramref name="value"/>; null or empty stays empty.</summary>
    public static string Encode(string? value) => string.IsNullOrEmpty(value) ? string.Empty : WebUtility.HtmlEncode(value);
}
