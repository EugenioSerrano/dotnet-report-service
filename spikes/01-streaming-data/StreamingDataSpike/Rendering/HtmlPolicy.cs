using System.Globalization;
using System.Net;

namespace StreamingDataSpike.Rendering;

/// <summary>
/// The one escaping policy of the measurement: exactly the output of
/// <see cref="WebUtility.HtmlEncode(string?)"/>. It encodes <c>&amp;</c>,
/// <c>&lt;</c>, <c>&gt;</c>, <c>"</c> and <c>'</c> as references, the
/// Latin-1 range U+00A0–U+00FF and every character above U+FFFF as numeric
/// references, and leaves every other character as raw UTF-8.
/// </summary>
internal static class HtmlPolicy
{
    /// <summary>Encodes a whole value.</summary>
    public static string Encode(string? value) => WebUtility.HtmlEncode(value) ?? string.Empty;

    /// <summary>Whether a Unicode scalar value is written as a reference.</summary>
    public static bool WillEncode(int scalar) =>
        scalar is '<' or '>' or '&' or '"' or '\''
        || scalar is >= 0xA0 and <= 0xFF
        || scalar >= 0x10000;

    /// <summary>The text written for one Unicode scalar value.</summary>
    public static string EncodeScalar(int scalar) => scalar switch
    {
        '<' => "&lt;",
        '>' => "&gt;",
        '&' => "&amp;",
        '"' => "&quot;",
        '\'' => "&#39;",
        _ when WillEncode(scalar) => "&#" + scalar.ToString(CultureInfo.InvariantCulture) + ";",
        _ => char.ConvertFromUtf32(scalar),
    };
}
