using System.Text;
using MarkdownSpike.Input;
using MarkdownSpike.Rendering;
using MarkdownSpike.Routes;

namespace MarkdownSpike.Checking;

/// <summary>
/// First render of a sample package's HTML parts: parses every part, renders the
/// package's sample data, and lists the elements, attributes and table
/// structure the parts produce.
/// </summary>
internal static class HtmlInventory
{
    private static readonly string[] TableElements = ["table", "thead", "tbody", "tfoot"];

    public static int Run(CommandLine line)
    {
        line.Allow("template", "data");
        string? template = line.Optional("template");
        string report;
        if (template is null)
        {
            var all = new StringBuilder();
            foreach (string package in new[] { "pilot-grouped", "pilot-simple" })
            {
                string folder = RepositoryPaths.HtmlTemplate(package);
                all.Append(Inventory(folder, Path.Combine(folder, "sample.json"))).Append('\n');
            }

            report = all.ToString();
            Directory.CreateDirectory(RepositoryPaths.Results);
            File.WriteAllText(Path.Combine(RepositoryPaths.Results, "html-inventory.txt"), report, new UTF8Encoding(false));
        }
        else
        {
            report = Inventory(template, line.Optional("data") ?? Path.Combine(template, "sample.json"));
        }

        Console.Write(report);
        return 0;
    }

    private static string Inventory(string packageFolder, string dataFile)
    {
        string htmlFolder = Path.Combine(packageFolder, "html");
        PartRenderer renderer = PartRenderer.Load(htmlFolder, "html", markdownFunctions: false);
        var collector = new Collector();
        long records;
        long groups;
        using (var reader = new EnvelopeReader(dataFile))
        {
            (records, groups) = PartSequencer.Run(reader, renderer, collector);
        }

        var elements = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var attributes = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var tokenizer = new HtmlTokenizer();
        tokenizer.Feed(collector.Document.ToString(), token =>
        {
            if (token.Kind != HtmlTokenKind.StartTag)
            {
                return;
            }

            elements[token.Name] = elements.GetValueOrDefault(token.Name) + 1;
            if (!attributes.TryGetValue(token.Name, out SortedSet<string>? names))
            {
                attributes[token.Name] = names = new SortedSet<string>(StringComparer.Ordinal);
            }

            foreach (KeyValuePair<string, string> attribute in token.Attributes)
            {
                names.Add(attribute.Key);
            }
        });
        tokenizer.Finish(_ => { });

        var report = new StringBuilder();
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(packageFolder));
        report.Append("package ").Append(name).Append('\n');
        report.Append("  parts parsed without errors: ").Append(string.Join(", ", collector.FirstText.Keys)).Append('\n');
        report.Append("  sample data: ").Append(Path.GetFileName(dataFile)).Append("  records=").Append(records).Append("  groups=").Append(groups).Append('\n');
        report.Append("  rendered HTML characters: ").Append(collector.Document.Length).Append('\n');
        report.Append("  elements (start tags, count):").Append('\n');
        foreach ((string element, long count) in elements)
        {
            string names = attributes.TryGetValue(element, out SortedSet<string>? set) && set.Count > 0 ? string.Join(" ", set) : "-";
            report.Append("    ").Append(element.PadRight(8)).Append(' ').Append(count.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(5)).Append("   attributes: ").Append(names).Append('\n');
        }

        report.Append("  table structure by part (opens / closes):").Append('\n');
        foreach ((PartKind kind, string text) in collector.FirstText)
        {
            (List<string> opens, List<string> closes, int rows) = TableTags(text);
            report.Append("    ").Append(kind.ToString().PadRight(14))
                .Append(" opens: ").Append(opens.Count == 0 ? "-" : string.Join(" ", opens))
                .Append("   closes: ").Append(closes.Count == 0 ? "-" : string.Join(" ", closes))
                .Append("   rows: ").Append(rows).Append('\n');
        }

        bool perGroup = collector.FirstText.TryGetValue(PartKind.GroupStart, out string? groupStart) && TableTags(groupStart).Opens.Contains("table");
        report.Append("  table layout: ").Append(perGroup ? "per-group (the group start part opens a table)" : "per-document (one table opened by the document start part)").Append('\n');
        report.Append("  column headers: ").Append(string.Join(" | ", collector.ColumnHeaders())).Append('\n');
        report.Append("  heading rows per group (group start): ").Append(collector.FirstText.TryGetValue(PartKind.GroupStart, out string? gs) ? TableTags(gs).Rows : 0).Append('\n');
        report.Append("  subtotal rows per group (group end): ").Append(collector.FirstText.TryGetValue(PartKind.GroupEnd, out string? ge) ? TableTags(ge).Rows : 0).Append('\n');
        report.Append("  tfoot rows: ").Append(elements.GetValueOrDefault("tfoot")).Append('\n');
        return report.ToString();
    }

    private static (List<string> Opens, List<string> Closes, int Rows) TableTags(string html)
    {
        var opens = new List<string>();
        var closes = new List<string>();
        int rows = 0;
        var tokenizer = new HtmlTokenizer();
        void Sink(HtmlToken token)
        {
            if (token.Kind == HtmlTokenKind.StartTag && token.Name == "tr")
            {
                rows++;
            }

            if (Array.IndexOf(TableElements, token.Name) < 0)
            {
                return;
            }

            if (token.Kind == HtmlTokenKind.StartTag)
            {
                opens.Add(token.Name);
            }
            else if (token.Kind == HtmlTokenKind.EndTag)
            {
                closes.Add(token.Name);
            }
        }

        tokenizer.Feed(html, Sink);
        tokenizer.Finish(Sink);
        return (opens, closes, rows);
    }

    private sealed class Collector : IPartSink
    {
        public StringBuilder Document { get; } = new();

        public Dictionary<PartKind, string> FirstText { get; } = [];

        public void OnPart(PartKind kind, string text, long recordIndex)
        {
            Document.Append(text);
            FirstText.TryAdd(kind, text);
        }

        public List<string> ColumnHeaders()
        {
            var headers = new List<string>();
            bool inHead = false;
            bool inCell = false;
            var cell = new StringBuilder();
            var tokenizer = new HtmlTokenizer();
            void Sink(HtmlToken token)
            {
                switch (token.Kind)
                {
                    case HtmlTokenKind.StartTag when token.Name == "thead":
                        inHead = true;
                        break;
                    case HtmlTokenKind.EndTag when token.Name == "thead":
                        inHead = false;
                        break;
                    case HtmlTokenKind.StartTag when inHead && token.Name == "th":
                        inCell = true;
                        cell.Clear();
                        break;
                    case HtmlTokenKind.EndTag when inHead && token.Name == "th":
                        inCell = false;
                        headers.Add(cell.ToString().Trim());
                        break;
                    case HtmlTokenKind.Text when inCell:
                        cell.Append(token.Text);
                        break;
                }
            }

            tokenizer.Feed(Document.ToString(), Sink);
            tokenizer.Finish(Sink);
            return headers;
        }
    }
}
