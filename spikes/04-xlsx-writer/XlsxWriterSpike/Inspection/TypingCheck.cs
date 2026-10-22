using System.Globalization;
using System.Text.Json;
using XlsxWriterSpike.Typing;
using XlsxWriterSpike.Writers;

namespace XlsxWriterSpike.Inspection;

/// <summary>
/// Small-scale checks of the typing rule, formula-like text and the row limit:
/// builds tiny envelopes, runs every writing candidate in-process on each, and
/// inspects the outputs. Prints one PASS/FAIL line per case and candidate.
/// </summary>
internal static class TypingCheck
{
    private const string Sheet = "items";

    private static readonly string[] FormulaLike =
    [
        "=1+1",
        "=SUM(A1:A9)",
        "+1+1",
        "-2+3",
        "@SUM(1,1)",
        "=HYPERLINK(\"http://example.invalid\",\"x\")",
    ];

    /// <summary>Runs every case; returns 0 when all pass.</summary>
    public static int Run(string workDirectory, TextWriter output)
    {
        var root = Path.Combine(workDirectory, "typing");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        var inputDirectory = Directory.CreateDirectory(Path.Combine(root, "input")).FullName;
        var outputDirectory = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;
        var keptDirectory = Directory.CreateDirectory(Path.Combine(root, "kept")).FullName;
        var profiles = Path.Combine(AppContext.BaseDirectory, "profiles");
        var strict = ColumnProfile.Load(Path.Combine(profiles, "volume-strict.json"));
        var text = ColumnProfile.Load(Path.Combine(profiles, "volume-exact-text.json"));

        var cases = Cases(strict, text);
        var passed = 0;
        var failed = 0;
        foreach (var check in cases)
        {
            var input = Path.Combine(inputDirectory, check.Id + ".json");
            WriteEnvelope(input, check.Records);
            foreach (var candidate in Candidates.WritingNames)
            {
                var folder = check.Keep ? keptDirectory : outputDirectory;
                var outputPath = Path.Combine(folder, $"{check.Id}-{candidate}.xlsx");
                var request = new WriteRequest(candidate, input, check.Profile, outputPath, check.Rows, check.Policy, check.SheetRowLimit, check.IgnoreHeaderCount);
                var outcome = WriteRunner.Execute(request);
                InspectionReport? report = null;
                string? failure;
                try
                {
                    if (outcome.ExitCode == 0 && outcome.OutputExists)
                    {
                        var source = new RecordSource(input, check.Profile);
                        report = Inspector.Inspect(outputPath, source, check.Rows, check.SheetRowLimit);
                        if (check.Keep)
                        {
                            Inspector.WriteProbes(report, check.Profile, Path.ChangeExtension(outputPath, ".probes.json"));
                        }
                    }

                    failure = Verify(check, outcome, report, outputPath);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or System.Xml.XmlException or SpikeException)
                {
                    failure = "inspection failed: " + exception.Message;
                }

                var detail = failure ?? Detail(outcome, report, check, outputPath);
                output.WriteLine($"{(failure is null ? "PASS" : "FAIL")} {check.Id} {candidate} - {check.Title} - {detail}");
                if (failure is null)
                {
                    passed++;
                }
                else
                {
                    failed++;
                }

                if (!check.Keep && File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
        }

        foreach (var candidate in Candidates.WritingNames)
        {
            var kept = Path.Combine(keptDirectory, $"C1-{candidate}.xlsx");
            if (File.Exists(kept))
            {
                using var package = new XlsxPackageReader(kept);
                foreach (var sheet in package.Sheets)
                {
                    foreach (var _ in package.ReadRows(sheet))
                    {
                    }
                }

                var types = string.Join(" ", package.Sheets.SelectMany(s => s.CellsByType).GroupBy(p => p.Key).Select(g => $"{g.Key}={g.Sum(p => p.Value)}"));
                output.WriteLine(
                    $"INFO {candidate} storage: sharedStrings={(package.SharedStringsPath is null ? "absent" : "present")} cellXfs={package.CellXfsCount?.ToString(CultureInfo.InvariantCulture) ?? "-"} cell types {types}");
            }
        }

        output.WriteLine($"typing-check: {passed} PASS, {failed} FAIL (kept outputs: {cases.Count(c => c.Keep)} cases x {Candidates.WritingNames.Length} candidates)");
        return failed == 0 ? 0 : 1;
    }

    private static string? Verify(Case check, WriteOutcome outcome, InspectionReport? report, string outputPath)
    {
        switch (check.Expect)
        {
            case Expectation.Ok:
                if (outcome.ExitCode != 0 || !outcome.OutputExists || report is null)
                {
                    return $"expected ok, got exit {outcome.ExitCode} {outcome.Message}";
                }

                if (report.Mismatches != 0 || report.Formulas != 0 || !report.HeadersOk || !report.SequenceContinuous)
                {
                    return $"mismatches={report.Mismatches} f={report.Formulas} headersOk={report.HeadersOk} sequence={report.SequenceContinuous} {string.Join("; ", report.FirstMismatches)}";
                }

                return check.Cells(outputPath, report);

            case Expectation.Limit:
                if (outcome.ExitCode != 3 || outcome.LimitError is not { } error)
                {
                    return $"expected exit 3 {OutputLimitExceededException.ErrorCode}, got exit {outcome.ExitCode} {outcome.Message}";
                }

                if (error.Reason != check.Reason || error.Row != check.Row || error.Column != check.Column)
                {
                    return $"expected reason={check.Reason} row={check.Row} column={check.Column}, got {error.Message}";
                }

                if (outcome.OutputExists || File.Exists(outputPath))
                {
                    return "an output file was left behind";
                }

                if (check.BeforeWriting is { } before && error.BeforeWriting != before)
                {
                    return $"expected beforeWriting={before}, got {error.BeforeWriting}";
                }

                return null;

            case Expectation.Failed:
                return outcome.ExitCode == 1 && !File.Exists(outputPath)
                    && outcome.Message?.Contains("does not match the declared type", StringComparison.Ordinal) == true
                    ? null
                    : $"expected exit 1 for a type mismatch and no file, got exit {outcome.ExitCode} file={File.Exists(outputPath)}: {outcome.Message}";

            default:
                return "unknown expectation";
        }
    }

    private static string Detail(WriteOutcome outcome, InspectionReport? report, Case check, string outputPath)
    {
        if (outcome.ExitCode != 0)
        {
            return $"exit {outcome.ExitCode}: {outcome.Message}; no output file"
                + (outcome.LimitError is { BeforeWriting: true } ? "; raised before writing" : string.Empty);
        }

        var sheets = string.Join(", ", report!.Sheets.Select(s => $"{s.Name}={s.Rows - 1}"));
        return $"exit 0; sheets {sheets}; mismatches 0; f=0" + (check.Note?.Invoke(outputPath, report) is { } note ? "; " + note : string.Empty);
    }

    private static List<Case> Cases(ColumnProfile strict, ColumnProfile text)
    {
        static Dictionary<string, string> Record(long sequence) => new(StringComparer.Ordinal)
        {
            ["sequence"] = sequence.ToString(CultureInfo.InvariantCulture),
            ["groupId"] = Json("G000001"),
            ["accountId"] = Json("00000123456789012345"),
            ["description"] = Json("plain text"),
            ["quantity"] = "2.5",
            ["amount"] = "10.25",
            ["reference"] = "12345",
        };

        static List<Dictionary<string, string>> One(string column, string raw)
        {
            var record = Record(1);
            record[column] = raw;
            return [record];
        }

        static List<Dictionary<string, string>> Many(int count) =>
            [.. Enumerable.Range(1, count).Select(i => Record(i))];

        static List<Dictionary<string, string>> Rows(params (string Column, string Raw)[] overrides)
        {
            var list = new List<Dictionary<string, string>>();
            foreach (var (column, raw) in overrides)
            {
                var record = Record(list.Count + 1);
                record[column] = raw;
                list.Add(record);
            }

            return list;
        }

        var longText = string.Concat(Enumerable.Repeat("abcdefghijklmnopqrstuvwxyz0123456789 ", 1000));
        var text32767 = longText[..32_767];
        var text32768 = longText[..32_768];
        var formulaRows = FormulaLike.Select(f => ("description", Json(f))).ToArray();

        return
        [
            Case.Ok("T1", "decimal with 15 significant digits is a numeric cell", strict, One("amount", "9876543210123.45"))
                .WithCells(Number("F2", "9876543210123.45")),
            Case.Limit("T2", "decimal with 16 significant digits fails", strict, One("amount", "98765432101234.56"), "precision", 1, "amount"),
            Case.Limit("T3", "decimal 0.1234567890123456 (16 digits) fails", strict, One("amount", "0.1234567890123456"), "precision", 1, "amount"),
            Case.Ok("T4", "trailing zeros: 1000.000000000000 is 1000 exactly", strict, One("quantity", "1000.000000000000"))
                .WithCells(Number("E2", "1000")),
            Case.Ok("T5", "negative and zero decimals", strict, Rows(("amount", "-0.01"), ("amount", "0")))
                .WithCells(Number("F2", "-0.01"), Number("F3", "0")),
            Case.Ok("T6", "integer with 15 digits is exact", strict, One("reference", "900719925474099"))
                .WithCells(Number("G2", "900719925474099")),
            Case.Limit("T7", "integer 9007199254740993 (16 digits, above 2^53) fails", strict, One("reference", "9007199254740993"), "precision", 1, "reference"),
            Case.Ok("T8", "string column keeps a 16-digit JSON number as text", text, One("reference", "9007199254740993"))
                .WithCells(Text("G2", "9007199254740993")),
            Case.Ok("T9", "string column keeps a 16-digit decimal as text", text, One("quantity", "1234.567890123456"))
                .WithCells(Text("E2", "1234.567890123456")),
            Case.Ok("T10", "string column keeps leading zeros", strict, One("accountId", Json("00000123456789012345")))
                .WithCells(Text("C2", "00000123456789012345")),
            Case.Ok("T11", "formula-like strings are text, never formulas", strict, Rows(formulaRows))
                .WithCells([.. FormulaLike.Select((f, i) => Text("D" + (i + 2).ToString(CultureInfo.InvariantCulture), f))]),
            Case.Ok("T12", "32 767 characters fit one cell", strict, One("description", Json(text32767)))
                .WithCells(Text("D2", text32767)),
            Case.Limit("T13", "32 768 characters fail", strict, One("description", Json(text32768)), "cell-length", 1, "description"),
            Case.Limit("T14", "2 500 rows over a 1 000-row sheet, policy error: fails before writing", strict, Many(2500), "rows", 1001, "-")
                .WithLimit(1000, RowLimitPolicy.Error, ignoreHeader: false, beforeWriting: true),
            Case.Limit("T15", "same with the header count ignored: streaming guard at record 1 001", strict, Many(2500), "rows", 1001, "-")
                .WithLimit(1000, RowLimitPolicy.Error, ignoreHeader: true, beforeWriting: false),
            Case.Ok("T16", "same with policy split: items 1 000, items-2 1 000, items-3 500", strict, Many(2500))
                .WithLimit(1000, RowLimitPolicy.Split, ignoreHeader: false, beforeWriting: null)
                .WithSheets(("items", 1000), ("items-2", 1000), ("items-3", 500))
                .Kept(),
            Case.Ok("T17", "exactly 1 000 rows fit a 1 000-row sheet", strict, Many(1000))
                .WithLimit(1000, RowLimitPolicy.Error, ignoreHeader: false, beforeWriting: null)
                .WithSheets(("items", 1000)),
            Case.Limit("T18", "decimal 1e-30 (underflows the decimal type) fails, never rounded to 0", strict, One("amount", "1e-30"), "precision", 1, "amount"),
            Case.Limit("T19", "decimal with 29 decimal places fails, never rounded", strict, One("amount", "0.00000000000000000000000000001"), "precision", 1, "amount"),
            Case.Limit("T20", "decimal 1E+30 (beyond the decimal range) fails", strict, One("amount", "1E+30"), "precision", 1, "amount"),
            Case.Limit("T21", "integer of 33 digits (cannot be parsed) fails", strict, One("reference", "100000000000000000000000000000000"), "precision", 1, "reference"),
            Case.Ok("T22", "decimal with an exponent (1.5E3) is the number 1500", strict, One("amount", "1.5E3"))
                .WithCells(Number("F2", "1500")),
            Case.Ok("T23", "negative zero is written as 0", strict, One("amount", "-0"))
                .WithCells(Number("F2", "0", noMinus: true)),
            Case.Failed("T24", "a fraction in an integer column is a type mismatch (exit 1)", strict, One("sequence", "12.5")),
            Case.Ok("C1", "combined numeric-profile cases kept for validation and Excel", strict, Rows(
                    [("amount", "9876543210123.45"), ("quantity", "1000.000000000000"), ("amount", "-0.01"), ("amount", "0"),
                     ("reference", "900719925474099"), ("accountId", Json("00000123456789012345")), .. formulaRows,
                     ("description", Json(text32767)), ("amount", "1.5E3"), ("amount", "-0")]))
                .Kept(),
            Case.Ok("C2", "combined text-profile cases kept for validation and Excel", text, Rows(
                    [("reference", "9007199254740993"), ("quantity", "1234.567890123456"), ("accountId", Json("00000123456789012345")), .. formulaRows]))
                .Kept(),
        ];
    }

    private static Func<string, InspectionReport, string?> Number(string reference, string expected, bool noMinus = false) => (path, _) =>
    {
        var cell = FindCell(path, Sheet, reference);
        if (cell is null || !cell.IsNumber || cell.HasFormula || cell.Text is null)
        {
            return $"{reference}: expected a numeric cell, found {(cell is null ? "no cell" : $"t={cell.Type} '{cell.Text}'")}";
        }

        if (!decimal.TryParse(cell.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || value != decimal.Parse(expected, NumberStyles.Float, CultureInfo.InvariantCulture))
        {
            return $"{reference}: expected value {expected}, found '{cell.Text}'";
        }

        return noMinus && cell.Text.StartsWith('-') ? $"{reference}: written as '{cell.Text}'" : null;
    };

    private static Func<string, InspectionReport, string?> Text(string reference, string expected) => (path, _) =>
    {
        var cell = FindCell(path, Sheet, reference);
        return cell is { IsText: true, HasFormula: false } && cell.Text == expected
            ? null
            : $"{reference}: expected text of length {expected.Length}, found {(cell is null ? "no cell" : $"t={cell.Type} length {cell.Text?.Length}")}";
    };

    private static CellRecord? FindCell(string path, string sheetName, string reference)
    {
        using var package = new XlsxPackageReader(path);
        var sheet = package.Sheets.Find(s => s.Name == sheetName);
        if (sheet is null)
        {
            return null;
        }

        foreach (var row in package.ReadRows(sheet))
        {
            var cell = row.Cells.Find(c => c.Reference == reference);
            if (cell is not null)
            {
                return cell;
            }
        }

        return null;
    }

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static void WriteEnvelope(string path, IReadOnlyList<Dictionary<string, string>> records)
    {
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream);
        json.WriteStartObject();
        json.WriteString("contractVersion", "1.0");
        json.WriteStartObject("header");
        json.WriteString("datasetId", "typing-check");
        json.WriteStartObject("totals");
        json.WriteNumber("recordCount", records.Count);
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteStartArray("items");
        foreach (var record in records)
        {
            json.WriteStartObject();
            foreach (var (name, raw) in record)
            {
                json.WritePropertyName(name);
                json.WriteRawValue(raw, skipInputValidation: true);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private enum Expectation
    {
        Ok,
        Limit,
        Failed,
    }

    private sealed record Case(string Id, string Title, ColumnProfile Profile, IReadOnlyList<Dictionary<string, string>> Records, Expectation Expect)
    {
        public string? Reason { get; init; }

        public long Row { get; init; }

        public string? Column { get; init; }

        public bool? BeforeWriting { get; init; }

        public long? Rows { get; init; }

        public RowLimitPolicy Policy { get; init; } = RowLimitPolicy.Error;

        public long SheetRowLimit { get; init; } = SheetPlan.MaxDataRowsPerSheet;

        public bool IgnoreHeaderCount { get; init; }

        public bool Keep { get; init; }

        public IReadOnlyList<Func<string, InspectionReport, string?>> CellChecks { get; init; } = [];

        public Func<string, InspectionReport, string?>? Note { get; init; }

        public static Case Ok(string id, string title, ColumnProfile profile, IReadOnlyList<Dictionary<string, string>> records) =>
            new(id, title, profile, records, Expectation.Ok);

        public static Case Limit(string id, string title, ColumnProfile profile, IReadOnlyList<Dictionary<string, string>> records, string reason, long row, string column) =>
            new(id, title, profile, records, Expectation.Limit) { Reason = reason, Row = row, Column = column, BeforeWriting = reason == "rows" ? null : false };

        public static Case Failed(string id, string title, ColumnProfile profile, IReadOnlyList<Dictionary<string, string>> records) =>
            new(id, title, profile, records, Expectation.Failed);

        public Case WithCells(params Func<string, InspectionReport, string?>[] checks) => this with
        {
            CellChecks = checks,
            Note = (path, _) =>
            {
                var cells = checks.Length;
                return $"{cells} cell check(s) passed";
            },
        };

        public Case WithLimit(long limit, RowLimitPolicy policy, bool ignoreHeader, bool? beforeWriting) => this with
        {
            SheetRowLimit = limit,
            Policy = policy,
            IgnoreHeaderCount = ignoreHeader,
            BeforeWriting = beforeWriting,
        };

        public Case WithSheets(params (string Name, long DataRows)[] sheets) => this with
        {
            CellChecks =
            [
                (_, report) =>
                {
                    var found = report.Sheets.Select(s => (s.Name, s.Rows - 1)).ToList();
                    return found.SequenceEqual(sheets)
                        ? null
                        : $"expected sheets {string.Join(", ", sheets.Select(s => $"{s.Name}={s.DataRows}"))}, found {string.Join(", ", found.Select(s => $"{s.Name}={s.Item2}"))}";
                },
            ],
        };

        public Case Kept() => this with { Keep = true };

        public string? Cells(string path, InspectionReport report)
        {
            foreach (var check in CellChecks)
            {
                if (check(path, report) is { } failure)
                {
                    return failure;
                }
            }

            return null;
        }
    }
}
