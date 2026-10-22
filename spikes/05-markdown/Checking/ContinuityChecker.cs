using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarkdownSpike.Input;
using MarkdownSpike.Rendering;
using MarkdownSpike.Routes;

namespace MarkdownSpike.Checking;

/// <summary>
/// Streams a Markdown output and its dataset side by side and checks that every
/// group comes out as rows of one continuous table: one separator per table, no
/// repeated header, the same cell count on every line, every record in order,
/// and only the template's heading and subtotal rows besides the records.
/// Only the current table header and the current record are held.
/// </summary>
internal static partial class ContinuityChecker
{
    private const int MaxFailures = 20;
    private const int MaxSideText = 64 * 1024;

    public static int Run(CommandLine line)
    {
        line.Allow("data", "output", "block-size", "subtotal-rows", "heading-rows", "tables");
        string data = line.Required("data");
        string output = line.Required("output");
        string? layout = line.Optional("tables");
        if (layout is not (null or "per-group" or "per-document"))
        {
            throw new UsageException("--tables must be per-group or per-document.");
        }

        CheckResult result;
        using (var markdown = new StreamReader(output, Encoding.UTF8))
        {
            result = Check(data, markdown, line.Int("block-size", ConvertCommand.DefaultBlockSize), OptionalCount(line, "subtotal-rows"), OptionalCount(line, "heading-rows"), layout);
        }

        Console.WriteLine(JsonSerializer.Serialize(result, ResultJson.Options));
        return 0;
    }

    /// <summary>
    /// Layout defaults per sample package, taken from the HTML inventory: both
    /// pilot packages open one table in the document start part; the grouped
    /// package adds one heading row per group (group start) and one subtotal
    /// row per group (group end).
    /// </summary>
    public static (string Layout, int HeadingRows, int SubtotalRows) Defaults(string datasetId) => datasetId switch
    {
        "pilot-grouped" => ("per-document", 1, 1),
        _ => ("per-document", 0, 0),
    };

    public static CheckResult Check(string dataFile, TextReader markdown, int blockSize, int? subtotalRows, int? headingRows, string? layout)
    {
        using var reader = new EnvelopeReader(dataFile);
        (string defaultLayout, int defaultHeading, int defaultSubtotal) = Defaults(reader.DatasetId);
        var state = new State(
            reader,
            blockSize,
            subtotalRows ?? defaultSubtotal,
            headingRows ?? defaultHeading,
            layout ?? defaultLayout);
        string? text;
        while ((text = markdown.ReadLine()) is not null)
        {
            state.OnLine(text);
        }

        return state.Finish();
    }

    /// <summary>Number of cells of a table line: pipes not escaped by a backslash, minus one.</summary>
    public static int CountCells(string line)
    {
        int pipes = 0;
        int backslashes = 0;
        foreach (char c in line)
        {
            if (c == '|' && backslashes % 2 == 0)
            {
                pipes++;
            }

            backslashes = c == '\\' ? backslashes + 1 : 0;
        }

        return Math.Max(pipes - 1, 0);
    }

    private static int? OptionalCount(CommandLine line, string name) =>
        line.Optional(name) is { } value
            ? int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int count) ? count : throw new UsageException($"Option --{name} needs a whole number.")
            : null;

    [GeneratedRegex("<[A-Za-z/]")]
    private static partial Regex RawHtmlPattern();

    private sealed record RecordInfo(long Index, string Group, string Description);

    private sealed class State
    {
        private readonly IEnumerator<JsonElement> records;
        private readonly EnvelopeReader reader;
        private readonly int blockSize;
        private readonly int subtotalRows;
        private readonly int headingRows;
        private readonly string layout;
        private readonly CheckResult result = new();
        private readonly StringBuilder textBefore = new();
        private readonly StringBuilder textAfter = new();
        private RecordInfo? expected;
        private RecordInfo? following;
        private long nextIndex;
        private string? currentGroup;
        private bool groupComplete = true;
        private int subtotalUsed;
        private int headingUsed;
        private long lineNumber;
        private bool inTable;
        private string headerLine = string.Empty;
        private int headerCells;
        private int tableLine;
        private int separators;
        private string? spanGroup;
        private long spanFirst;
        private long spanLast;

        public State(EnvelopeReader reader, int blockSize, int subtotalRows, int headingRows, string layout)
        {
            this.reader = reader;
            this.blockSize = blockSize;
            this.subtotalRows = subtotalRows;
            this.headingRows = headingRows;
            this.layout = layout;
            result.Layout = layout;
            records = reader.ReadItems().GetEnumerator();
            expected = NextRecord();
            following = NextRecord();
        }

        public void OnLine(string line)
        {
            lineNumber++;
            if (RawHtmlPattern().IsMatch(line))
            {
                result.RawHtmlLines++;
                Fail($"line {lineNumber}: raw HTML tag in the output");
            }

            if (!line.StartsWith('|'))
            {
                if (inTable)
                {
                    inTable = false;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    StringBuilder side = result.Tables == 0 ? textBefore : textAfter;
                    if (side.Length < MaxSideText)
                    {
                        side.Append(line).Append(' ');
                    }
                }

                return;
            }

            if (!inTable)
            {
                inTable = true;
                result.Tables++;
                textAfter.Clear();
                headerLine = MarkdownText.CollapseWhitespace(line);
                headerCells = CountCells(line);
                tableLine = 0;
                separators = 0;
                return;
            }

            tableLine++;
            if (FragmentStitchingConverter.IsSeparator(line))
            {
                separators++;
                result.SeparatorsPerTableMax = Math.Max(result.SeparatorsPerTableMax, separators);
                if (tableLine != 1)
                {
                    Fail($"line {lineNumber}: separator line inside a table");
                }

                return;
            }

            if (tableLine == 1)
            {
                Fail($"line {lineNumber}: table without a separator line");
            }

            if (MarkdownText.CollapseWhitespace(line) == headerLine)
            {
                result.RepeatedHeaders++;
                Fail($"line {lineNumber}: table header repeated");
                return;
            }

            if (CountCells(line) != headerCells)
            {
                result.CellCountMismatches++;
                Fail($"line {lineNumber}: {CountCells(line)} cells, header has {headerCells}");
            }

            MatchRow(line);
        }

        public CheckResult Finish()
        {
            while (expected is not null)
            {
                result.RecordsMissing++;
                if (result.RecordsMissing == 1)
                {
                    Fail($"record {expected.Index + 1} (group {expected.Group}) and later records are missing");
                }

                Advance();
            }

            CloseSpan();
            result.ExpectedTables = layout == "per-group" ? result.Groups : 1;
            string before = MarkdownText.CollapseWhitespace(MarkdownText.Unescape(textBefore.ToString()));
            string after = MarkdownText.CollapseWhitespace(MarkdownText.Unescape(textAfter.ToString()));
            JsonElement header = reader.Header;
            string title = MarkdownText.CollapseWhitespace(header.GetProperty("reportTitle").GetString() ?? string.Empty);
            string customer = MarkdownText.CollapseWhitespace(header.GetProperty("customer").GetProperty("name").GetString() ?? string.Empty);
            string total = header.GetProperty("totals").GetProperty("amount").GetRawText();
            result.HeaderFieldsFound = (before.Contains(title, StringComparison.Ordinal) ? 1 : 0)
                + (before.Contains(customer, StringComparison.Ordinal) ? 1 : 0)
                + (after.Contains(total, StringComparison.Ordinal) ? 1 : 0);

            if (result.Tables != result.ExpectedTables)
            {
                Fail($"{result.Tables} tables, expected {result.ExpectedTables}");
            }

            bool continuous = result.Tables == result.ExpectedTables
                && result.SeparatorsPerTableMax <= 1
                && result.RepeatedHeaders == 0
                && result.CellCountMismatches == 0
                && result.RecordsMissing == 0
                && result.UnexpectedRows == 0;
            result.Outcome = continuous ? "continuous" : "broken";
            records.Dispose();
            return result;
        }

        private void MatchRow(string line)
        {
            result.RowsChecked++;
            string text = MarkdownText.CollapseWhitespace(MarkdownText.Unescape(line));
            if (expected is not null && text.Contains(expected.Description, StringComparison.Ordinal))
            {
                if (expected.Group != currentGroup)
                {
                    currentGroup = expected.Group;
                    subtotalUsed = 0;
                }

                headingUsed = 0;
                groupComplete = following is null || following.Group != expected.Group;
                Advance();
                return;
            }

            if (groupComplete)
            {
                if (currentGroup is { Length: > 0 } && subtotalUsed < subtotalRows && headingUsed == 0 && text.Contains(currentGroup, StringComparison.Ordinal))
                {
                    subtotalUsed++;
                    result.SubtotalRowsFound++;
                    return;
                }

                if (expected is { Group.Length: > 0 } && headingUsed < headingRows && text.Contains(expected.Group, StringComparison.Ordinal))
                {
                    headingUsed++;
                    result.HeadingRowsFound++;
                    return;
                }
            }

            result.UnexpectedRows++;
            Fail($"line {lineNumber}: unexpected row (expected record {(expected is null ? "none" : (expected.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))}, group {expected?.Group ?? currentGroup})");
        }

        private void Advance()
        {
            expected = following;
            following = NextRecord();
        }

        private RecordInfo? NextRecord()
        {
            if (!records.MoveNext())
            {
                return null;
            }

            JsonElement item = records.Current;
            string group = item.TryGetProperty("groupId", out JsonElement g) ? g.GetString() ?? string.Empty : string.Empty;
            string description = MarkdownText.CollapseWhitespace(item.GetProperty("description").GetString() ?? string.Empty);
            var info = new RecordInfo(nextIndex++, group, description);
            TrackSpan(info);
            return info;
        }

        private void TrackSpan(RecordInfo record)
        {
            if (record.Group.Length == 0)
            {
                return;
            }

            if (record.Group != spanGroup)
            {
                CloseSpan();
                result.Groups++;
                spanGroup = record.Group;
                spanFirst = record.Index;
            }

            spanLast = record.Index;
        }

        private void CloseSpan()
        {
            if (spanGroup is null)
            {
                return;
            }

            long span = (spanLast / blockSize) - (spanFirst / blockSize) + 1;
            if (span > result.MaxBlocksSpanned)
            {
                result.MaxBlocksSpanned = span;
                result.MaxSpanGroup = spanGroup;
            }

            spanGroup = null;
        }

        private void Fail(string message)
        {
            if (result.Failures.Count < MaxFailures)
            {
                result.Failures.Add(message);
            }
        }
    }
}

/// <summary>The one JSON line printed by a check.</summary>
internal sealed class CheckResult
{
    public string Layout { get; set; } = string.Empty;

    public int Tables { get; set; }

    public long ExpectedTables { get; set; }

    public long Groups { get; set; }

    public long MaxBlocksSpanned { get; set; }

    public string? MaxSpanGroup { get; set; }

    public int SeparatorsPerTableMax { get; set; }

    public long RepeatedHeaders { get; set; }

    public long RowsChecked { get; set; }

    /// <summary>Group heading rows found where the template places them (before a group's first record).</summary>
    public long HeadingRowsFound { get; set; }

    /// <summary>Subtotal rows found where the template places them (after a group's last record).</summary>
    public long SubtotalRowsFound { get; set; }

    public long RecordsMissing { get; set; }

    public long UnexpectedRows { get; set; }

    public long CellCountMismatches { get; set; }

    public long RawHtmlLines { get; set; }

    public int HeaderFieldsFound { get; set; }

    public string Outcome { get; set; } = "broken";

    public List<string> Failures { get; } = [];
}
