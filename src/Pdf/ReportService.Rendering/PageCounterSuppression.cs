using System.Text;

namespace ReportService.Rendering;

/// <summary>
/// The override a multi-block part gets so its own page counters print nothing: it
/// reads the document's page rules, finds the margin boxes whose content carries
/// <c>counter(page)</c> or <c>counter(pages)</c>, and empties exactly those boxes.
/// A margin box carrying anything else - a running header, a fixed label - is left
/// untouched. A single-block output never asks for the override: its template's
/// own counters are part of what it prints.
/// </summary>
public static class PageCounterSuppression
{
    /// <summary>The page-margin boxes CSS Paged Media defines.</summary>
    private static readonly string[] MarginBoxes =
    [
        "top-left-corner", "top-left", "top-center", "top-right", "top-right-corner",
        "bottom-left-corner", "bottom-left", "bottom-center", "bottom-right", "bottom-right-corner",
        "left-top", "left-middle", "left-bottom",
        "right-top", "right-middle", "right-bottom",
    ];

    /// <summary>The override for <paramref name="document"/>, or the empty string when it declares no page counter.</summary>
    /// <param name="document">The composed document, with the package's styles and rules.</param>
    public static string OverrideFor(string document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var boxes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in AtRuleBodies(document, 0, document.Length, "page"))
        {
            foreach (var box in MarginBoxes)
            {
                foreach (var body in AtRuleBodies(document, page.Start, page.End, box))
                {
                    if (CarriesPageCounter(body.Text))
                    {
                        boxes.Add(box);
                        break;
                    }
                }
            }
        }

        if (boxes.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder("@page { ");
        foreach (var box in MarginBoxes)
        {
            if (boxes.Contains(box))
            {
                builder.Append('@').Append(box).Append(" { content: none !important; } ");
            }
        }

        return builder.Append('}').ToString();
    }

    private static bool CarriesPageCounter(string body)
    {
        var compact = new string([.. body.Where(character => !char.IsWhiteSpace(character))]);
        return compact.Contains("counter(page)", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("counter(pages)", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the bodies of an at-rule inside a range: every occurrence of
    /// <c>@name</c> followed by a braced block, matched with nesting. HTML tags and
    /// strings are not parsed; a page rule is CSS text and the scanner only needs its
    /// braces, which a stylesheet balances.
    /// </summary>
    private static IEnumerable<(int Start, int End, string Text)> AtRuleBodies(
        string text,
        int from,
        int to,
        string name)
    {
        var needle = "@" + name;
        var index = from;
        while (index < to)
        {
            var found = text.IndexOf(needle, index, to - index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                yield break;
            }

            var open = text.IndexOf('{', found + needle.Length);
            if (open < 0 || open >= to)
            {
                yield break;
            }

            var close = MatchBrace(text, open, to);
            if (close < 0)
            {
                yield break;
            }

            yield return (open + 1, close, text[(open + 1)..close]);
            index = close + 1;
        }
    }

    private static int MatchBrace(string text, int open, int to)
    {
        var depth = 0;
        for (var index = open; index < to; index++)
        {
            switch (text[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return index;
                    }

                    break;
            }
        }

        return -1;
    }
}
