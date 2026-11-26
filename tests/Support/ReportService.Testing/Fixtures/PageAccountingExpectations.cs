using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReportService.Pdf.Verification;
using ReportService.Testing.TemplatePackages;

namespace ReportService.Testing.Fixtures;

/// <summary>One record of an account-taking snapshot: its ordinal, its group and its exact amount.</summary>
/// <param name="Ordinal">The record's ordinal in the snapshot, from one.</param>
/// <param name="Group">The record's group key.</param>
/// <param name="Amount">The record's exact amount.</param>
public sealed record AccountedRecord(long Ordinal, string Group, decimal Amount);

/// <summary>One page's expected accounting values, in exact decimals.</summary>
/// <param name="Page">The page's number, from one.</param>
/// <param name="BroughtForward">The running total of every amount before the page.</param>
/// <param name="Subtotal">The sum of the records that ended on the page.</param>
/// <param name="CarriedForward">The running total after the page.</param>
public sealed record AccountedPage(int Page, decimal BroughtForward, decimal Subtotal, decimal CarriedForward);

/// <summary>One group's expected total and the record that ends it.</summary>
/// <param name="Key">The group's key.</param>
/// <param name="Total">The sum of the group's records.</param>
/// <param name="LastOrdinal">The ordinal of the group's last record.</param>
public sealed record AccountedGroupTotal(string Key, decimal Total, long LastOrdinal);

/// <summary>The page-accounting declaration the fixture writes and the rendering and e2e cases read.</summary>
/// <param name="AmountField">The record field the totals sum.</param>
/// <param name="GroupKey">The record field the records are grouped by.</param>
/// <param name="Scale">The fraction digits the printed values carry.</param>
/// <param name="Culture">The culture the band's values are written in.</param>
/// <param name="BroughtForwardLabel">The band's brought-forward label.</param>
/// <param name="PageSubtotalLabel">The band's page-subtotal label.</param>
/// <param name="CarriedForwardLabel">The band's carried-forward label.</param>
/// <param name="NumberingFormat">The numbering wording the assembly stamps.</param>
/// <param name="NumberingCulture">The culture the numbering is written in.</param>
/// <param name="NumberingLeftMm">The numbering area's left edge, in millimetres.</param>
/// <param name="NumberingTopMm">The numbering area's top edge, in millimetres.</param>
/// <param name="NumberingWidthMm">The numbering area's width, in millimetres.</param>
/// <param name="NumberingHeightMm">The numbering area's height, in millimetres.</param>
public sealed record PageAccountingDeclaration(
    string AmountField,
    string GroupKey,
    int Scale,
    string Culture,
    string BroughtForwardLabel,
    string PageSubtotalLabel,
    string CarriedForwardLabel,
    string NumberingFormat,
    string NumberingCulture,
    double NumberingLeftMm,
    double NumberingTopMm,
    double NumberingWidthMm,
    double NumberingHeightMm)
{
    /// <summary>The declaration committed beside the fixture package.</summary>
    public static string FileName => "page-accounting.json";

    /// <summary>Reads the declaration from the test support's expectation folder.</summary>
    public static PageAccountingDeclaration Load()
    {
        var path = Path.Combine(
            RepositoryPaths.Root,
            "tests",
            "Support",
            "ReportService.Testing",
            "OracleExpectations",
            FileName);
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("The accounting declaration is not a JSON object.");
        var footer = root["footer"] as JsonObject
            ?? throw new InvalidDataException("The accounting declaration holds no footer.");
        var labels = footer["labels"] as JsonObject
            ?? throw new InvalidDataException("The accounting declaration holds no labels.");
        var numbering = root["numbering"] as JsonObject
            ?? throw new InvalidDataException("The accounting declaration holds no numbering.");
        var area = numbering["area"] as JsonObject
            ?? throw new InvalidDataException("The accounting declaration holds no numbering area.");
        return new PageAccountingDeclaration(
            Text(root, "amountField"),
            Text(root, "groupKey"),
            (int)Number(root, "scale"),
            Text(root, "culture"),
            Text(labels, "broughtForward"),
            Text(labels, "pageSubtotal"),
            Text(labels, "carriedForward"),
            Text(numbering, "format"),
            Text(numbering, "culture"),
            Number(area, "leftMm"),
            Number(area, "topMm"),
            Number(area, "widthMm"),
            Number(area, "heightMm"));
    }

    private static string Text(JsonObject node, string name) =>
        node[name]?.GetValue<string>()
        ?? throw new InvalidDataException($"The accounting declaration holds no '{name}'.");

    private static double Number(JsonObject node, string name) =>
        node[name]?.GetValue<double>()
        ?? throw new InvalidDataException($"The accounting declaration holds no '{name}'.");
}

/// <summary>The page each record's row started on and the page it ended on, read from the printed document.</summary>
/// <param name="StartPages">The page each record's ordinal was read on.</param>
/// <param name="EndPages">The page each record's amount was read on, where its row ended.</param>
public sealed record PageAccountingAttribution(
    IReadOnlyDictionary<long, int> StartPages,
    IReadOnlyDictionary<long, int> EndPages);

/// <summary>The expected accounting values of one printed document, computed from its source records.</summary>
/// <param name="Records">The records, in ordinal order.</param>
/// <param name="Pages">Each page's expected values.</param>
/// <param name="Groups">Each group's expected total, including the group that ends last.</param>
/// <param name="GrandTotal">The exact sum of every record's amount.</param>
/// <param name="Opening">The opening carried-forward the block was rendered with.</param>
/// <param name="Scale">The fraction digits the printed values carry.</param>
/// <param name="Culture">The culture the printed values are written in.</param>
public sealed record PageAccountingExpected(
    IReadOnlyList<AccountedRecord> Records,
    IReadOnlyList<AccountedPage> Pages,
    IReadOnlyList<AccountedGroupTotal> Groups,
    decimal GrandTotal,
    decimal Opening,
    int Scale,
    string Culture)
{
    /// <summary>The band text of <paramref name="value"/> in the declared culture and scale, with no group separator.</summary>
    /// <param name="value">The value.</param>
    public string BandText(decimal value) => PageAccountingExpectations.FormatBand(value, Scale, Culture);

    /// <summary>The text the row prints its amount with: the exact invariant decimal at the declared scale.</summary>
    /// <param name="value">The amount.</param>
    public string RowText(decimal value) => PageAccountingExpectations.FormatRow(value, Scale);
}

/// <summary>
/// The independent accounting oracle of the rendering and end-to-end cases: it reads the
/// records of a snapshot, attributes every record to the page its row ended on from the
/// printed document's own words (the row's ordinal starts it, its amount ends it), and
/// computes each page's subtotal, the brought/carried chain, each group's total and the
/// grand total in exact decimals. It shares no code with the product's verification: the
/// expected values come from the source records and the page attribution only, and a
/// printed value is read strictly under the declared scale and culture.
/// </summary>
public static class PageAccountingExpectations
{
    /// <summary>Reads the records of the snapshot at <paramref name="path"/>.</summary>
    /// <param name="path">The snapshot's file.</param>
    /// <param name="amountField">The record field the totals sum.</param>
    /// <param name="groupKey">The record field the records are grouped by.</param>
    public static IReadOnlyList<AccountedRecord> ReadRecords(string path, string amountField, string groupKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("The snapshot is not a JSON object.");
        var items = root["items"] as JsonArray
            ?? throw new InvalidDataException("The snapshot holds no items.");
        var records = new List<AccountedRecord>(items.Count);
        foreach (var item in items)
        {
            if (item is not JsonObject record)
            {
                throw new InvalidDataException("A snapshot record is not a JSON object.");
            }

            // The row prints the block's ordinal (block.recordOrdinal), which for a block that
            // begins at the snapshot's first record is the record's position, not necessarily
            // the sequence the producer wrote.
            var ordinal = (long)records.Count + 1;
            var amount = record[amountField]!.GetValue<decimal>();
            var group = record[groupKey]?.GetValue<string>() ?? string.Empty;
            records.Add(new AccountedRecord(ordinal, group, amount));
        }

        return records;
    }

    /// <summary>
    /// Attributes every record to the page its row started on and the page it ended on: a
    /// content word that is the record's ordinal starts the row, and the next word equal to
    /// the record's amount text is where it ends (the layout prints the amount on the row's
    /// last part).
    /// </summary>
    /// <param name="reading">The printed document as the reader read it.</param>
    /// <param name="expected">The expected values, whose records and texts the attribution follows.</param>
    public static PageAccountingAttribution Attribute(PdfReading reading, PageAccountingExpected expected)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(expected);
        var byOrdinal = expected.Records.ToDictionary(record => record.Ordinal, record => record);
        var byRowText = expected.Records.ToLookup(
            record => expected.RowText(record.Amount),
            record => record.Ordinal,
            StringComparer.Ordinal);
        var starts = new Dictionary<long, int>();
        var ends = new Dictionary<long, int>();
        AccountedRecord? current = null;
        foreach (var page in reading.Pages)
        {
            foreach (var word in page.Words.Where(word => word.Band == ReadingBand.Content))
            {
                var text = word.Text;
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal)
                    && byOrdinal.ContainsKey(ordinal)
                    && !ends.ContainsKey(ordinal))
                {
                    current = byOrdinal[ordinal];
                    starts[ordinal] = page.Number;
                    continue;
                }

                if (current is not null
                    && byRowText[text].Contains(current.Ordinal))
                {
                    ends[current.Ordinal] = page.Number;
                    current = null;
                }
            }
        }

        return new PageAccountingAttribution(starts, ends);
    }

    /// <summary>Computes the expected values from the records and the page each record ended on.</summary>
    /// <param name="records">The records, in ordinal order.</param>
    /// <param name="endPages">The page each record ended on.</param>
    /// <param name="pageCount">How many pages the document prints.</param>
    /// <param name="opening">The opening carried-forward.</param>
    /// <param name="scale">The fraction digits the printed values carry.</param>
    /// <param name="culture">The culture the printed values are written in.</param>
    public static PageAccountingExpected Compute(
        IReadOnlyList<AccountedRecord> records,
        IReadOnlyDictionary<long, int> endPages,
        int pageCount,
        decimal opening,
        int scale,
        string culture)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(endPages);
        var pages = new List<AccountedPage>(pageCount);
        var brought = opening;
        for (var page = 1; page <= pageCount; page++)
        {
            var subtotal = records
                .Where(record => endPages.TryGetValue(record.Ordinal, out var end) && end == page)
                .Sum(record => record.Amount);
            pages.Add(new AccountedPage(page, brought, subtotal, brought + subtotal));
            brought += subtotal;
        }

        var groups = new List<AccountedGroupTotal>();
        foreach (var group in records.GroupBy(record => record.Group))
        {
            groups.Add(new AccountedGroupTotal(group.Key, group.Sum(record => record.Amount), group.Max(record => record.Ordinal)));
        }

        return new PageAccountingExpected(
            records,
            pages,
            groups,
            records.Sum(record => record.Amount),
            opening,
            scale,
            culture);
    }

    /// <summary>The band text of <paramref name="value"/>: the declared scale, the culture's separator, no group separator.</summary>
    /// <param name="value">The value.</param>
    /// <param name="scale">The fraction digits.</param>
    /// <param name="culture">The culture.</param>
    public static string FormatBand(decimal value, int scale, string culture)
    {
        var info = CultureInfo.GetCultureInfo(culture);
        return value.ToString("F" + scale.ToString(CultureInfo.InvariantCulture), info);
    }

    /// <summary>The row's amount text: the exact invariant decimal at the declared scale.</summary>
    /// <param name="value">The value.</param>
    /// <param name="scale">The fraction digits.</param>
    public static string FormatRow(decimal value, int scale) =>
        value.ToString("F" + scale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a printed band value strictly: exactly the declared fraction digits after the
    /// culture's decimal separator and no group separator, or nothing.
    /// </summary>
    /// <param name="text">The printed text.</param>
    /// <param name="scale">The fraction digits.</param>
    /// <param name="culture">The culture.</param>
    /// <param name="value">The value read.</param>
    public static bool TryParseBand(string? text, int scale, string culture, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var separator = CultureInfo.GetCultureInfo(culture).NumberFormat.NumberDecimalSeparator;
        var negative = text[0] == '-';
        var digits = negative ? text[1..] : text;
        string integer;
        string fraction;
        if (scale == 0)
        {
            integer = digits;
            fraction = string.Empty;
            if (digits.Contains(separator, StringComparison.Ordinal))
            {
                return false;
            }
        }
        else
        {
            var at = digits.IndexOf(separator, StringComparison.Ordinal);
            if (at <= 0 || at != digits.LastIndexOf(separator, StringComparison.Ordinal))
            {
                return false;
            }

            integer = digits[..at];
            fraction = digits[(at + separator.Length)..];
            if (fraction.Length != scale)
            {
                return false;
            }
        }

        if (integer.Length == 0
            || integer.Any(character => character is < '0' or > '9')
            || fraction.Any(character => character is < '0' or > '9'))
        {
            return false;
        }

        var invariant = (negative ? "-" : string.Empty)
            + integer
            + (scale == 0 ? string.Empty : "." + fraction);
        return decimal.TryParse(invariant, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The three labelled values a page's footer band printed, or <see langword="null"/> for a missing line.</summary>
    /// <param name="page">The page as it was read.</param>
    /// <param name="declaration">The declaration of the labels.</param>
    public static (string? BroughtForward, string? PageSubtotal, string? CarriedForward) ReadBand(
        PrintedPage page,
        PageAccountingDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(declaration);
        var words = page.Words
            .Where(word => word.Band == ReadingBand.Footer)
            .Select(word => word.Text)
            .ToList();
        return (
            ValueAfter(words, declaration.BroughtForwardLabel),
            ValueAfter(words, declaration.PageSubtotalLabel),
            ValueAfter(words, declaration.CarriedForwardLabel));
    }

    /// <summary>Whether the word's centre lies inside the declared numbering area, in points.</summary>
    /// <param name="word">The printed word.</param>
    /// <param name="declaration">The declaration of the area.</param>
    public static bool InsideNumberingArea(PrintedWord word, PageAccountingDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(declaration);
        const double perMm = 72d / 25.4d;
        var left = declaration.NumberingLeftMm * perMm;
        var top = declaration.NumberingTopMm * perMm;
        var width = declaration.NumberingWidthMm * perMm;
        var height = declaration.NumberingHeightMm * perMm;
        var x = (word.Left + word.Right) / 2d;
        var y = word.VerticalCentre;
        return x >= left && x <= left + width && y >= top && y <= top + height;
    }

    /// <summary>The words of the numbering area on <paramref name="page"/>, in painting order.</summary>
    /// <param name="page">The page as it was read.</param>
    /// <param name="declaration">The declaration of the area.</param>
    public static IReadOnlyList<string> NumberingWords(PrintedPage page, PageAccountingDeclaration declaration) =>
        [.. page.Words.Where(word => InsideNumberingArea(word, declaration)).Select(word => word.Text)];

    /// <summary>The word that follows the label's own words in the footer band.</summary>
    private static string? ValueAfter(IReadOnlyList<string> words, string label)
    {
        var labelWords = label
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalisation.Normalise)
            .Where(text => text.Length > 0)
            .ToList();
        if (labelWords.Count == 0)
        {
            return null;
        }

        var normalised = words.Select(Normalisation.Normalise).ToList();
        for (var start = 0; start + labelWords.Count < normalised.Count; start++)
        {
            var matches = true;
            for (var offset = 0; offset < labelWords.Count; offset++)
            {
                if (!string.Equals(normalised[start + offset], labelWords[offset], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return words[start + labelWords.Count];
            }
        }

        return null;
    }
}
