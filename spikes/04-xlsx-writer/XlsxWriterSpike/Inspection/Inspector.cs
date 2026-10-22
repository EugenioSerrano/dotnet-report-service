using System.Globalization;
using System.Text;
using System.Text.Json;
using XlsxWriterSpike.Typing;
using XlsxWriterSpike.Writers;

namespace XlsxWriterSpike.Inspection;

/// <summary>One cell the Excel open check reads back.</summary>
/// <param name="Sheet">Sheet name.</param>
/// <param name="Cell">A1 reference.</param>
/// <param name="Kind"><c>text</c> or <c>number</c>.</param>
/// <param name="Expected">Expected text, or the invariant number text.</param>
internal sealed record Probe(string Sheet, string Cell, string Kind, string Expected);

/// <summary>The result of inspecting one file.</summary>
internal sealed class InspectionReport
{
    /// <summary>File name.</summary>
    public required string File { get; init; }

    /// <summary>Package parts.</summary>
    public required IReadOnlyList<string> Parts { get; init; }

    /// <summary>Whether a shared-strings part exists.</summary>
    public bool SharedStrings { get; init; }

    /// <summary>Uncompressed size of the shared-strings part.</summary>
    public long SharedStringsBytes { get; init; }

    /// <summary><c>count</c> / <c>uniqueCount</c> of the shared-strings table.</summary>
    public string? SharedStringsCounts { get; init; }

    /// <summary>Cell formats in the stylesheet.</summary>
    public int? CellXfs { get; init; }

    /// <summary>Sheets with their counts.</summary>
    public required IReadOnlyList<SheetInfo> Sheets { get; init; }

    /// <summary><c>f</c> elements in all sheets.</summary>
    public long Formulas => Sheets.Sum(s => s.Formulas);

    /// <summary>Whether the cells were compared with the source.</summary>
    public bool Compared { get; init; }

    /// <summary>Data rows compared.</summary>
    public long DataRows { get; init; }

    /// <summary>Cells that differ from the source (rows missing or extra count as one each).</summary>
    public long Mismatches { get; init; }

    /// <summary>The first five mismatches.</summary>
    public IReadOnlyList<string> FirstMismatches { get; init; } = [];

    /// <summary>Whether every sheet starts with the header row.</summary>
    public bool HeadersOk { get; init; }

    /// <summary>Whether the <c>sequence</c> column runs 1, 2, 3, ... across sheets.</summary>
    public bool SequenceContinuous { get; init; }

    /// <summary>Cells for the Excel check.</summary>
    public IReadOnlyList<Probe> Probes { get; init; } = [];

    /// <summary>Human-readable lines.</summary>
    public IEnumerable<string> Lines()
    {
        yield return $"inspect file={File}";
        yield return "  parts: " + string.Join(" ", Parts);
        yield return SharedStrings
            ? $"  sharedStrings: present bytes={SharedStringsBytes} {SharedStringsCounts}"
            : "  sharedStrings: absent";
        yield return $"  cellXfs: {(CellXfs?.ToString(CultureInfo.InvariantCulture) ?? "no styles part")}";
        foreach (var sheet in Sheets)
        {
            var types = string.Join(" ", sheet.CellsByType.Select(p => $"{p.Key}={p.Value}"));
            yield return $"  sheet {sheet.Name}: rows={sheet.Rows} cells {types} f={sheet.Formulas}";
        }

        if (Compared)
        {
            yield return $"  compare: dataRows={DataRows} mismatches={Mismatches} headersOk={Bool(HeadersOk)} sequenceContinuous={Bool(SequenceContinuous)}";
            foreach (var mismatch in FirstMismatches)
            {
                yield return "    mismatch " + mismatch;
            }
        }
    }

    private static string Bool(bool value) => value ? "true" : "false";
}

/// <summary>Inspects a file and, with a source, compares every data cell with the typed source.</summary>
internal static class Inspector
{
    private const int MaxProbes = 50;
    private static readonly char[] FormulaPrefixes = ['=', '+', '-', '@'];

    /// <summary>Inspects <paramref name="path"/>; compares with <paramref name="source"/> when given.</summary>
    public static InspectionReport Inspect(string path, RecordSource? source, long? rows, long sheetRowLimit)
    {
        using var package = new XlsxPackageReader(path);
        if (source is null)
        {
            foreach (var sheet in package.Sheets)
            {
                foreach (var _ in package.ReadRows(sheet))
                {
                }
            }

            return Report(path, package, compared: false, 0, 0, [], headersOk: false, sequenceOk: false, []);
        }

        var profile = source.Profile;
        var headerCount = source.ReadHeaderCount();
        var plan = SheetPlan.Create(profile.SheetName, headerCount, rows, sheetRowLimit, RowLimitPolicy.Split, false);
        var letters = profile.Columns.Select((_, i) => WorksheetMarkup.ColumnLetters(i)).ToArray();
        var sequenceColumn = profile.Columns.ToList().FindIndex(c => c.Name == "sequence");
        var small = Math.Min(rows ?? long.MaxValue, headerCount ?? long.MaxValue) <= 20;
        var probes = new List<Probe>();
        var probedPrefixes = new HashSet<char>();
        var mismatches = new List<string>();
        long mismatchCount = 0;
        long dataRows = 0;
        var headersOk = true;
        var sequenceOk = sequenceColumn >= 0;
        decimal previousSequence = 0;
        var firstRow = new string?[profile.Columns.Count];

        void Mismatch(string text)
        {
            mismatchCount++;
            if (mismatches.Count < 5)
            {
                mismatches.Add(text);
            }
        }

        void AddProbe(Probe probe)
        {
            if (probes.Count < MaxProbes && !probes.Exists(p => p.Sheet == probe.Sheet && p.Cell == probe.Cell))
            {
                probes.Add(probe);
            }
        }

        if (package.Sheets.Count != plan.Sheets.Count)
        {
            Mismatch($"sheets: expected {plan.Sheets.Count}, found {package.Sheets.Count}");
        }

        for (var s = 0; s < Math.Min(package.Sheets.Count, plan.Sheets.Count); s++)
        {
            var sheet = package.Sheets[s];
            var window = plan.Sheets[s];
            if (sheet.Name != window.Name)
            {
                Mismatch($"sheet {s + 1}: expected name '{window.Name}', found '{sheet.Name}'");
            }

            using var expected = source.Rows(window, plan.RowLimit).GetEnumerator();
            var first = true;
            long sheetRows = 0;
            TypedCell lastSequence = default;
            foreach (var row in package.ReadRows(sheet))
            {
                if (first)
                {
                    first = false;
                    headersOk &= HeaderMatches(row, profile);
                    if (s == 0)
                    {
                        AddProbe(new Probe(sheet.Name, "A1", "text", profile.Columns[0].Name));
                    }

                    continue;
                }

                if (!expected.MoveNext())
                {
                    Mismatch($"{sheet.Name} row {row.RowNumber}: extra row");
                    continue;
                }

                sheetRows++;
                dataRows++;
                var typed = expected.Current;
                var rowNumber = sheetRows + 1;
                for (var i = 0; i < typed.Cells.Length; i++)
                {
                    var actual = row.Cells.Find(c => c.Column == i);
                    var cell = typed.Cells[i];
                    var reference = letters[i] + rowNumber.ToString(CultureInfo.InvariantCulture);
                    if (!CellMatches(cell, actual))
                    {
                        Mismatch($"{sheet.Name}!{reference}: expected {Describe(cell)}, found {Describe(actual)}");
                    }

                    if (cell.Kind == CellKind.Empty)
                    {
                        continue;
                    }

                    var probe = new Probe(sheet.Name, reference, cell.Kind == CellKind.Number ? "number" : "text", cell.Text!);
                    var column = profile.Columns[i].Name;
                    if (s == 0 && sheetRows == 1)
                    {
                        firstRow[i] = cell.Text;
                    }

                    if ((small && i != sequenceColumn && cell.Text != firstRow[i])
                        || (s == 0 && sheetRows == 1 && column is "accountId" or "quantity" or "amount" or "reference")
                        || (column == "description" && cell.Text is { Length: > 0 } t && Array.IndexOf(FormulaPrefixes, t[0]) >= 0 && probedPrefixes.Add(t[0])))
                    {
                        AddProbe(probe);
                    }
                }

                if (sequenceColumn >= 0)
                {
                    lastSequence = typed.Cells[sequenceColumn];
                    var actual = row.Cells.Find(c => c.Column == sequenceColumn);
                    if (actual?.Text is null
                        || !decimal.TryParse(actual.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sequence)
                        || sequence != previousSequence + 1)
                    {
                        sequenceOk = false;
                    }
                    else
                    {
                        previousSequence = sequence;
                    }
                }
            }

            if (first)
            {
                headersOk = false;
                Mismatch($"{sheet.Name}: no header row");
            }

            long missing = 0;
            while (expected.MoveNext())
            {
                missing++;
            }

            if (missing > 0)
            {
                Mismatch($"{sheet.Name}: {missing} expected rows missing");
            }

            if (sequenceColumn >= 0 && sheetRows > 0)
            {
                AddProbe(new Probe(sheet.Name, letters[sequenceColumn] + (sheetRows + 1).ToString(CultureInfo.InvariantCulture), "number", lastSequence.Text!));
            }
        }

        return Report(path, package, compared: true, dataRows, mismatchCount, mismatches, headersOk, sequenceOk, probes);
    }

    /// <summary>Writes the probe file read by the Excel open check.</summary>
    public static void WriteProbes(InspectionReport report, ColumnProfile profile, string path)
    {
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("file", report.File);
        json.WriteStartArray("sheets");
        foreach (var sheet in report.Sheets)
        {
            json.WriteStartObject();
            json.WriteString("name", sheet.Name);
            json.WriteNumber("rows", sheet.Rows);
            json.WriteNumber("columns", profile.Columns.Count);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartArray("probes");
        foreach (var probe in report.Probes)
        {
            json.WriteStartObject();
            json.WriteString("sheet", probe.Sheet);
            json.WriteString("cell", probe.Cell);
            json.WriteString("kind", probe.Kind);
            json.WriteString("expected", probe.Expected);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static InspectionReport Report(
        string path, XlsxPackageReader package, bool compared, long dataRows, long mismatchCount, List<string> mismatches,
        bool headersOk, bool sequenceOk, List<Probe> probes) => new()
        {
            File = Path.GetFileName(path),
            Parts = package.Parts,
            SharedStrings = package.SharedStringsPath is not null,
            SharedStringsBytes = package.SharedStringsBytes,
            SharedStringsCounts = package.SharedStringsPath is null ? null : $"count={package.SharedStringsCount} uniqueCount={package.SharedStringsUniqueCount}",
            CellXfs = package.CellXfsCount,
            Sheets = package.Sheets,
            Compared = compared,
            DataRows = dataRows,
            Mismatches = mismatchCount,
            FirstMismatches = mismatches,
            HeadersOk = compared && headersOk,
            SequenceContinuous = compared && sequenceOk,
            Probes = probes,
        };

    private static bool HeaderMatches(SheetRow row, ColumnProfile profile)
    {
        for (var i = 0; i < profile.Columns.Count; i++)
        {
            var cell = row.Cells.Find(c => c.Column == i);
            if (cell is null || !cell.IsText || cell.Text != profile.Columns[i].Name)
            {
                return false;
            }
        }

        return row.Cells.Count == profile.Columns.Count;
    }

    private static bool CellMatches(TypedCell expected, CellRecord? actual) => expected.Kind switch
    {
        CellKind.Empty => actual is null || (actual.Text is null or { Length: 0 }),
        CellKind.Text => actual is { IsText: true, HasFormula: false } && actual.Text == expected.Text,
        CellKind.Number => actual is { IsNumber: true, HasFormula: false, Text: { } v }
            && decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && number == expected.Number,
        _ => false,
    };

    private static string Describe(TypedCell cell) => cell.Kind switch
    {
        CellKind.Empty => "no cell",
        CellKind.Number => "number " + cell.Text,
        _ => "text '" + Shorten(cell.Text) + "'",
    };

    private static string Describe(CellRecord? cell) =>
        cell is null ? "no cell" : $"t={cell.Type ?? "n"}{(cell.HasFormula ? " with formula" : string.Empty)} '{Shorten(cell.Text)}'";

    private static string Shorten(string? text) =>
        text is null ? "null" : text.Length <= 40 ? text : new StringBuilder(text, 0, 40, 43).Append("...").ToString();
}
