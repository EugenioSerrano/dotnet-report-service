using System.Text;

namespace ReportService.Pdf.Verification.PageCodes;

/// <summary>
/// The content the product accepts from a page-code generator, whatever generator
/// drew it: ASCII; numeric operands; the path, painting, colour and graphics-state
/// operators of the closed set below; balanced save/restore; and a bounded size.
/// No text, no image, no named resource and no external object can appear, so the
/// code adds nothing to a page beyond its own form and its drawing.
/// </summary>
public static class PageCodeContentValidator
{
    /// <summary>The largest content a generator may return, uncompressed.</summary>
    public const int MaximumContentBytes = 64 * 1024;

    /// <summary>The largest form, as stored (compressed stream plus its dictionary).</summary>
    public const int MaximumStoredBytes = 16 * 1024;

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "q", "Q", "cm", "w", "J", "j", "M", "d",
        "m", "l", "c", "v", "y", "h", "re",
        "f", "f*", "F", "B", "B*", "b", "b*", "n", "S", "s",
        "g", "G", "rg", "RG", "k", "K",
        "W", "W*",
    };

    /// <summary>Checks one generator's content.</summary>
    /// <param name="content">The content stream bytes.</param>
    /// <returns><see langword="null"/> when accepted, else the reason it is not.</returns>
    public static string? Problem(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0)
        {
            return "the code's content is empty";
        }

        if (content.Length > MaximumContentBytes)
        {
            return "the code's content is larger than " + MaximumContentBytes + " bytes";
        }

        foreach (var current in content)
        {
            if (current > 0x7E || (current < 0x20 && !IsWhite(current)))
            {
                return "the code's content is not printable ASCII";
            }
        }

        var depth = 0;
        var operands = 0;
        var index = 0;
        while (index < content.Length)
        {
            var current = content[index];
            if (IsWhite(current) || IsBracket(current))
            {
                // A dash array's brackets only group operands of d.
                index++;
                continue;
            }

            if (current == (byte)'%')
            {
                while (index < content.Length && content[index] is not (byte)'\n' and not (byte)'\r')
                {
                    index++;
                }

                continue;
            }

            var start = index;
            while (index < content.Length && !IsWhite(content[index]) && !IsBracket(content[index]))
            {
                index++;
            }

            var token = Encoding.ASCII.GetString(content[start..index]);
            if (IsNumber(token))
            {
                operands++;
                continue;
            }

            if (!Allowed.Contains(token))
            {
                return "the code's content uses an operator that is not allowed: " + Shorten(token);
            }

            if (token == "q")
            {
                depth++;
            }
            else if (token == "Q")
            {
                depth--;
                if (depth < 0)
                {
                    return "the code's content restores a state it never saved";
                }
            }

            operands = 0;
        }

        if (depth != 0)
        {
            return "the code's content does not restore every state it saves";
        }

        return operands != 0 ? "the code's content ends with operands and no operator" : null;
    }

    private static bool IsWhite(byte value) =>
        value is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)'\f';

    private static bool IsBracket(byte value) => value is (byte)'[' or (byte)']';

    private static bool IsNumber(string token)
    {
        var digits = 0;
        var points = 0;
        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];
            if (char.IsAsciiDigit(c))
            {
                digits++;
            }
            else if (c is '-' or '+')
            {
                if (i != 0)
                {
                    return false;
                }
            }
            else if (c == '.')
            {
                points++;
            }
            else
            {
                return false;
            }
        }

        return digits > 0 && points <= 1;
    }

    private static string Shorten(string token) => token.Length <= 16 ? token : token[..16];
}
