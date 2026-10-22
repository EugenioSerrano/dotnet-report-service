using System.Text;
using System.Text.RegularExpressions;
using MarkdownSpike.Rendering;
using ReverseMarkdown;

namespace MarkdownSpike.Routes;

/// <summary>
/// Library per fragment, with table stitching. Each fragment is wrapped in the
/// elements left open by the previous fragments (and, inside a table, in that
/// table's recorded header), converted on its own by the library, and the
/// synthetic table header of a continuation fragment is removed so its rows
/// continue the table already written.
/// </summary>
internal sealed partial class FragmentStitchingConverter : IFragmentSink
{
    private readonly Converter converter = new(CreateConfig());
    private readonly TextWriter output;
    private readonly HtmlTokenizer scanner = new();
    private readonly Action<HtmlToken> onToken;
    private readonly List<OpenElement> open = [];
    private StringBuilder? headCapture;
    private OpenElement? captureTable;
    private bool anyOutput;

    public FragmentStitchingConverter(TextWriter output)
    {
        this.output = output;
        onToken = Track;
    }

    /// <summary>Continuation fragments whose synthetic header could not be found in the library output.</summary>
    public int MissingSyntheticHeaders { get; private set; }

    /// <summary>
    /// The library configuration used by the library routes. Pipe tables come
    /// from the GitHub-compatible table writer that version 6.2.1 selects with
    /// the default flavor (the legacy GitHub switch maps to it); the separate
    /// GitHub flavor value of that version keeps tables as raw HTML.
    /// </summary>
    public static Config CreateConfig()
    {
        var config = new Config { Flavor = Config.MarkdownFlavor.Default };
        config.Tags.Unknown = Config.UnknownTagsOption.Drop;
        config.Formatting.RemoveComments = true;
        config.Formatting.OutputLineEnding = "\n";
        config.Tables.WithoutHeaderRow = Config.TableWithoutHeaderRowHandlingOption.EmptyRow;
        return config;
    }

    public static bool IsSeparator(string line) => SeparatorPattern().IsMatch(line);

    public void OnFragment(string html, FragmentKind kind)
    {
        bool beganInTable = InTable();
        string prefix = Prefix();
        scanner.Feed(html, onToken);
        string markdown = converter.Convert(prefix + html + Suffix());
        Stitch(markdown, beganInTable);
    }

    public void Complete()
    {
        scanner.Finish(onToken);
        if (anyOutput)
        {
            output.Write('\n');
        }
    }

    /// <summary>Stitches one converted fragment onto the output (public for the self-test).</summary>
    public void Stitch(string markdown, bool beganInTable)
    {
        List<string> lines = [.. markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];
        if (beganInTable)
        {
            int header = lines.FindIndex(line => line.StartsWith('|'));
            if (header >= 0 && header + 1 < lines.Count && IsSeparator(lines[header + 1]))
            {
                lines.RemoveRange(header, 2);
            }
            else
            {
                MissingSyntheticHeaders++;
            }
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            return;
        }

        if (anyOutput)
        {
            bool continuesTable = beganInTable && lines[0].StartsWith('|');
            output.Write(continuesTable ? "\n" : "\n\n");
        }

        output.Write(string.Join('\n', lines));
        anyOutput = true;
    }

    [GeneratedRegex(@"^\|(\s*:?-{3,}:?\s*\|)+$")]
    private static partial Regex SeparatorPattern();

    private bool InTable() => open.Exists(element => element.Name == "table");

    private string Prefix()
    {
        var prefix = new StringBuilder();
        foreach (OpenElement element in open)
        {
            prefix.Append(element.StartTag);
            if (element.Name == "table" && element.Head is not null)
            {
                prefix.Append(element.Head);
            }
        }

        return prefix.ToString();
    }

    private string Suffix()
    {
        var suffix = new StringBuilder();
        for (int i = open.Count - 1; i >= 0; i--)
        {
            suffix.Append("</").Append(open[i].Name).Append('>');
        }

        return suffix.ToString();
    }

    private void Track(HtmlToken token)
    {
        headCapture?.Append(token.Raw);
        switch (token.Kind)
        {
            case HtmlTokenKind.StartTag when !token.SelfClosing && !HtmlTokenizer.IsVoid(token.Name):
                var element = new OpenElement(token.Name, token.Raw);
                open.Add(element);
                if (token.Name == "thead" && headCapture is null)
                {
                    captureTable = open.FindLast(e => e.Name == "table");
                    if (captureTable is not null)
                    {
                        headCapture = new StringBuilder(token.Raw);
                    }
                }

                break;
            case HtmlTokenKind.EndTag:
                int index = open.FindLastIndex(e => e.Name == token.Name);
                if (index >= 0)
                {
                    open.RemoveRange(index, open.Count - index);
                }

                if (token.Name == "thead" && headCapture is not null && captureTable is not null)
                {
                    captureTable.Head = headCapture.ToString();
                    headCapture = null;
                    captureTable = null;
                }

                break;
        }
    }

    private sealed class OpenElement(string name, string startTag)
    {
        public string Name { get; } = name;

        public string StartTag { get; } = startTag;

        /// <summary>The recorded table header markup (tables only).</summary>
        public string? Head { get; set; }
    }
}
