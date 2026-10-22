using System.Globalization;
using System.Text;
using MarkdownSpike.Rendering;

namespace MarkdownSpike.Routes;

/// <summary>
/// Writes Markdown from HTML tokens with a state machine and no document tree.
/// Converted elements: headings, paragraphs, line breaks, lists, tables
/// (caption, head, body and foot rows), links, images, strong and emphasis,
/// inline code and horizontal rules. Any other element is a wrapper: its tags
/// are dropped, its text is kept, and its name is counted as a loss. The
/// content of the document head is metadata and is not written. Only the
/// current block, the current table row and the current cell are buffered,
/// so a table continues naturally across fragments.
/// </summary>
internal sealed class StreamingMarkdownWriter(TextWriter output)
{
    private readonly StringBuilder inline = new();
    private readonly Stack<(bool Ordered, int Count)> lists = new();
    private readonly Stack<(int Start, string Href)> links = new();
    private BlockKind block = BlockKind.None;
    private int headingLevel;
    private string listMarker = string.Empty;
    private bool anyOutput;
    private bool lastWasListItem;
    private int headDepth;
    private TableState? table;

    private enum BlockKind
    {
        None,
        Paragraph,
        Heading,
        ListItem,
    }

    /// <summary>Element names and attribute names (prefixed with "@") dropped, with counts.</summary>
    public SortedDictionary<string, long> Losses { get; } = new(StringComparer.Ordinal);

    public void OnToken(HtmlToken token)
    {
        switch (token.Kind)
        {
            case HtmlTokenKind.StartTag:
                CountAttributes(token);
                Start(token);
                break;
            case HtmlTokenKind.EndTag:
                End(token.Name);
                break;
            default:
                Text(token.Text);
                break;
        }
    }

    public void Finish()
    {
        FlushBlock();
        if (anyOutput)
        {
            output.Write('\n');
        }
    }

    private StringBuilder Target => table?.Cell ?? inline;

    private void Start(HtmlToken token)
    {
        string name = token.Name;
        if (headDepth > 0 || name == "head")
        {
            if (name == "head")
            {
                headDepth++;
            }

            Lose(name);
            return;
        }

        switch (name)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                FlushBlock();
                block = BlockKind.Heading;
                headingLevel = name[1] - '0';
                break;
            case "p":
                FlushBlock();
                block = BlockKind.Paragraph;
                break;
            case "br":
                if (table?.Cell is not null)
                {
                    table.Cell.Append(' ');
                }
                else if (block != BlockKind.None)
                {
                    inline.Append("\\\n");
                }

                break;
            case "ul" or "ol":
                FlushBlock();
                lists.Push((name == "ol", 0));
                break;
            case "li":
                FlushBlock();
                if (lists.Count == 0)
                {
                    lists.Push((false, 0));
                }

                (bool ordered, int count) = lists.Pop();
                lists.Push((ordered, count + 1));
                listMarker = new string(' ', 2 * (lists.Count - 1)) + (ordered ? (count + 1).ToString(CultureInfo.InvariantCulture) + ". " : "- ");
                block = BlockKind.ListItem;
                break;
            case "strong" or "b":
                Target.Append("**");
                break;
            case "em" or "i":
                Target.Append('_');
                break;
            case "code":
                Target.Append('`');
                break;
            case "a":
                EnsureInlineBlock();
                links.Push((Target.Length, token.Attribute("href") ?? string.Empty));
                break;
            case "img":
                EnsureInlineBlock();
                Target.Append("![").Append(MarkdownText.Escape(token.Attribute("alt"))).Append("](")
                    .Append(token.Attribute("src") ?? string.Empty).Append(')');
                break;
            case "hr":
                FlushBlock();
                WriteLine("---", tight: false);
                break;
            case "table":
                FlushBlock();
                table = new TableState();
                break;
            case "caption":
                block = BlockKind.Paragraph;
                break;
            case "thead" when table is not null:
                table.InHead = true;
                break;
            case "tbody" or "tfoot" when table is not null:
                break;
            case "tr" when table is not null:
                table.Row = [];
                table.RowHasHeaderCell = false;
                break;
            case "td" or "th" when table?.Row is not null:
                table.Cell = new StringBuilder();
                table.CellSpan = int.TryParse(token.Attribute("colspan"), NumberStyles.None, CultureInfo.InvariantCulture, out int span) && span > 1 ? span : 1;
                table.RowHasHeaderCell |= name == "th";
                break;
            default:
                Lose(name);
                break;
        }
    }

    private void End(string name)
    {
        if (headDepth > 0)
        {
            if (name == "head")
            {
                headDepth--;
            }

            return;
        }

        switch (name)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "p" or "li" or "caption":
                FlushBlock();
                break;
            case "ul" or "ol":
                FlushBlock();
                if (lists.Count > 0)
                {
                    lists.Pop();
                }

                break;
            case "strong" or "b":
                Target.Append("**");
                break;
            case "em" or "i":
                Target.Append('_');
                break;
            case "code":
                Target.Append('`');
                break;
            case "a" when links.Count > 0:
                (int start, string href) = links.Pop();
                StringBuilder target = Target;
                string label = target.ToString(start, target.Length - start);
                target.Length = start;
                target.Append('[').Append(label).Append("](").Append(href).Append(')');
                break;
            case "thead" when table is not null:
                table.InHead = false;
                break;
            case "td" or "th" when table?.Cell is not null && table.Row is not null:
                table.Row.Add(table.Cell.ToString().Trim(' '));
                for (int i = 1; i < table.CellSpan; i++)
                {
                    table.Row.Add(string.Empty);
                }

                table.Cell = null;
                break;
            case "tr" when table?.Row is not null:
                WriteRow(table, table.Row);
                table.Row = null;
                break;
            case "table":
                table = null;
                break;
        }
    }

    private void Text(string text)
    {
        if (headDepth > 0)
        {
            return;
        }

        bool whitespaceOnly = string.IsNullOrWhiteSpace(text);
        if (table is not null && table.Cell is null && block == BlockKind.None)
        {
            if (!whitespaceOnly)
            {
                Lose("text outside table cells");
            }

            return;
        }

        if (table?.Cell is null && block == BlockKind.None)
        {
            if (whitespaceOnly)
            {
                return;
            }

            block = BlockKind.Paragraph;
        }

        StringBuilder target = Target;
        var collapsed = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if ((collapsed.Length > 0 && collapsed[^1] == ' ') || (collapsed.Length == 0 && (target.Length == 0 || target[^1] == ' ')))
                {
                    continue;
                }

                collapsed.Append(' ');
            }
            else
            {
                collapsed.Append(c);
            }
        }

        MarkdownText.AppendEscaped(target, collapsed.ToString());
    }

    private void EnsureInlineBlock()
    {
        if (table?.Cell is null && block == BlockKind.None)
        {
            block = BlockKind.Paragraph;
        }
    }

    private void WriteRow(TableState state, List<string> cells)
    {
        if (!state.HeaderWritten)
        {
            state.HeaderWritten = true;
            if (state.InHead || state.RowHasHeaderCell)
            {
                WriteLine(TableLine(cells), tight: false);
                WriteLine(Separator(cells.Count), tight: true);
                return;
            }

            WriteLine(TableLine(Enumerable.Repeat(string.Empty, cells.Count)), tight: false);
            WriteLine(Separator(cells.Count), tight: true);
        }

        WriteLine(TableLine(cells), tight: true);
    }

    private static string TableLine(IEnumerable<string> cells) => "| " + string.Join(" | ", cells) + " |";

    private static string Separator(int count) => "|" + string.Concat(Enumerable.Repeat(" --- |", Math.Max(count, 1)));

    private void FlushBlock()
    {
        if (block == BlockKind.None)
        {
            return;
        }

        string content = inline.ToString().Trim(' ');
        inline.Clear();
        BlockKind kind = block;
        block = BlockKind.None;
        if (content.Length == 0)
        {
            return;
        }

        switch (kind)
        {
            case BlockKind.Heading:
                WriteLine(new string('#', headingLevel) + " " + content, tight: false);
                break;
            case BlockKind.ListItem:
                WriteLine(listMarker + content, tight: lastWasListItem);
                lastWasListItem = true;
                return;
            default:
                WriteLine(content, tight: false);
                break;
        }
    }

    private void WriteLine(string line, bool tight)
    {
        if (anyOutput)
        {
            output.Write(tight ? "\n" : "\n\n");
        }

        output.Write(line);
        anyOutput = true;
        lastWasListItem = false;
    }

    private void CountAttributes(HtmlToken token)
    {
        foreach (KeyValuePair<string, string> attribute in token.Attributes)
        {
            bool kept = (token.Name, attribute.Key) is ("a", "href") or ("img", "src") or ("img", "alt");
            if (!kept)
            {
                Lose($"@{attribute.Key} on {token.Name}");
            }
        }
    }

    private void Lose(string what) => Losses[what] = Losses.GetValueOrDefault(what) + 1;

    private sealed class TableState
    {
        public bool HeaderWritten { get; set; }

        public bool InHead { get; set; }

        public List<string>? Row { get; set; }

        public bool RowHasHeaderCell { get; set; }

        public StringBuilder? Cell { get; set; }

        public int CellSpan { get; set; } = 1;
    }
}
