using System.Text;
using ReportService.Templates;

namespace ReportService.Rendering;

/// <summary>
/// The override a multi-block part gets so its own page counters print nothing: it
/// reads the styles the printed document actually carries - the document's own
/// <c>&lt;style&gt;</c> rules and the stylesheets it links, resolved against the
/// package exactly as the printer serves them - finds the <c>@page</c> margin boxes
/// whose content carries <c>counter(page)</c> or <c>counter(pages)</c>, and empties
/// exactly those boxes. A margin box carrying anything else - a running header, a
/// fixed label - is left untouched. A single-block output never asks for the
/// override: its template's own counters are part of what it prints.
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

    /// <summary>
    /// The override for <paramref name="document"/>, or the empty string when it
    /// declares no page counter.
    /// </summary>
    /// <param name="document">The composed document, with the package's styles and rules.</param>
    /// <param name="files">The files of the taken package, from which the linked stylesheets are read.</param>
    public static string OverrideFor(string document, ITemplatePackageFiles files)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(files);

        var styles = DeclaredStyles(document, files);
        var boxes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in AtRuleBodies(styles, 0, styles.Length, "page"))
        {
            foreach (var box in MarginBoxes)
            {
                foreach (var body in AtRuleBodies(styles, page.Start, page.End, box))
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

    /// <summary>
    /// The CSS the printed document actually applies: each inline <c>&lt;style&gt;</c>
    /// body, plus each linked stylesheet, resolved against the document's own URL and
    /// served from the package through the same route policy the printer uses. A link
    /// that names another origin, a query, a fragment or a file the package does not
    /// hold is skipped, exactly as the printer aborts it.
    /// </summary>
    private static string DeclaredStyles(string document, ITemplatePackageFiles files)
    {
        var builder = new StringBuilder(document.Length);
        foreach (var body in StyleBodies(document))
        {
            builder.Append(body).Append('\n');
        }

        var policy = new PackageRoutePolicy(files);
        foreach (var href in LinkedStylesheets(document))
        {
            if (PackagePath(href, policy) is not { } path)
            {
                continue;
            }

            try
            {
                builder.Append(files.ReadAllText(path)).Append('\n');
            }
            catch (TemplatePackageException)
            {
                // The route policy served it, so the printer would too; a read that
                // fails after that bounds nothing the print does not already bound.
            }
        }

        return builder.ToString();
    }

    private static string? PackagePath(string href, PackageRoutePolicy policy)
    {
        var reference = href.Trim();
        if (reference.Length == 0)
        {
            return null;
        }

        try
        {
            var url = new Uri(new Uri(PackageRoutePolicy.DocumentUrl, UriKind.Absolute), reference);
            var decision = policy.Decide(url.AbsoluteUri);
            return decision.Kind == RouteKind.Served ? decision.PackagePath : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>The bodies of the document's inline style elements, in document order.</summary>
    private static IEnumerable<string> StyleBodies(string html)
    {
        var index = 0;
        while ((index = html.IndexOf("<style", index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var open = html.IndexOf('>', index + 6);
            if (open < 0)
            {
                yield break;
            }

            var close = html.IndexOf("</style", open + 1, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                yield break;
            }

            yield return html[(open + 1)..close];
            index = close + 7;
        }
    }

    /// <summary>The href of every <c>&lt;link&gt;</c> whose rel names a stylesheet, in document order.</summary>
    private static IEnumerable<string> LinkedStylesheets(string html)
    {
        var index = 0;
        while ((index = html.IndexOf("<link", index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var next = index + 5;
            if (next < html.Length && !char.IsWhiteSpace(html[next]) && html[next] != '>' && html[next] != '/')
            {
                index = next;
                continue;
            }

            var close = html.IndexOf('>', next);
            if (close < 0)
            {
                yield break;
            }

            var tag = html[next..close];
            var rel = Attribute(tag, "rel");
            var href = Attribute(tag, "href");
            if (rel is not null
                && href is not null
                && rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Any(token => string.Equals(token, "stylesheet", StringComparison.OrdinalIgnoreCase)))
            {
                yield return href;
            }

            index = close + 1;
        }
    }

    /// <summary>
    /// The value of <paramref name="name"/> in one tag's text, quoted or unquoted, or
    /// <see langword="null"/> when the tag does not carry it. Attribute names are
    /// matched whole, so <c>href</c> never answers for a <c>data-href</c>.
    /// </summary>
    private static string? Attribute(string tag, string name)
    {
        var search = 0;
        while (search < tag.Length)
        {
            var found = tag.IndexOf(name, search, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return null;
            }

            var afterName = found + name.Length;
            var beforeIsSpace = found == 0 || char.IsWhiteSpace(tag[found - 1]);
            var afterIsBoundary = afterName >= tag.Length || char.IsWhiteSpace(tag[afterName]) || tag[afterName] == '=';
            if (!beforeIsSpace || !afterIsBoundary)
            {
                search = afterName;
                continue;
            }

            var cursor = afterName;
            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
            {
                cursor++;
            }

            if (cursor >= tag.Length || tag[cursor] != '=')
            {
                search = afterName;
                continue;
            }

            cursor++;
            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
            {
                cursor++;
            }

            if (cursor >= tag.Length)
            {
                return null;
            }

            if (tag[cursor] is '"' or '\'')
            {
                var quote = tag[cursor];
                var end = tag.IndexOf(quote, cursor + 1);
                return end < 0 ? null : tag[(cursor + 1)..end];
            }

            var start = cursor;
            while (cursor < tag.Length && !char.IsWhiteSpace(tag[cursor]) && tag[cursor] != '>')
            {
                cursor++;
            }

            return cursor > start ? tag[start..cursor] : null;
        }

        return null;
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
