using System.Net;
using System.Text;

namespace MarkdownSpike.Rendering;

/// <summary>
/// One escaping rule for Markdown text, shared by the own streaming converter
/// and the Markdown template parts, so their outputs are comparable.
/// </summary>
internal static class MarkdownText
{
    /// <summary>Plain text in, Markdown-safe text out, with leading and trailing spaces removed.</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length + 16);
        AppendEscaped(builder, text);
        return builder.ToString().Trim(' ');
    }

    /// <summary>
    /// Appends the escaped text without trimming: the characters that open HTML
    /// become entities, Markdown punctuation is backslash-escaped, and each run
    /// of line breaks and tabs becomes one space.
    /// </summary>
    public static void AppendEscaped(StringBuilder builder, string text)
    {
        bool inBreak = false;
        foreach (char c in text)
        {
            if (c is '\r' or '\n' or '\t')
            {
                if (!inBreak)
                {
                    builder.Append(' ');
                }

                inBreak = true;
                continue;
            }

            inBreak = false;
            switch (c)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '\\' or '`' or '*' or '_' or '[' or ']' or '|':
                    builder.Append('\\').Append(c);
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
    }

    /// <summary>The inverse of <see cref="Escape"/>: removes backslash escapes, then decodes entities.</summary>
    public static string Unescape(string markdown)
    {
        var builder = new StringBuilder(markdown.Length);
        for (int i = 0; i < markdown.Length; i++)
        {
            char c = markdown[i];
            if (c == '\\' && i + 1 < markdown.Length && IsAsciiPunctuation(markdown[i + 1]))
            {
                builder.Append(markdown[++i]);
            }
            else
            {
                builder.Append(c);
            }
        }

        return WebUtility.HtmlDecode(builder.ToString());
    }

    /// <summary>Collapses every run of white space to one space and trims the ends.</summary>
    public static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsAsciiPunctuation(char c) =>
        c is (>= '!' and <= '/') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~');
}
