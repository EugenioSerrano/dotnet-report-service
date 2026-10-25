using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PageAccountingCheck;

/// <summary>A page box as qpdf wrote it: the text of its four numbers and their decimal values.</summary>
internal sealed record BoxValue(string Text, decimal[] Numbers);

/// <summary>One page of a PDF as qpdf's JSON shows it: object, boxes, annotations, content streams, keys.</summary>
internal sealed class QpdfPage
{
    public int Page { get; init; }

    public string Object { get; init; } = string.Empty;

    public BoxValue? MediaBox { get; init; }

    public BoxValue? CropBox { get; init; }

    /// <summary>Each annotation: subtype, action type, address (URI) and rectangle text.</summary>
    public List<(string? Subtype, string? Action, string? Uri, string? Rect)> Annotations { get; init; } = [];

    public List<string> Contents { get; init; } = [];

    /// <summary>Each key of the page dictionary and the shape of its value (indirect references shown as "R").</summary>
    public SortedDictionary<string, string> Keys { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The shape of the page's /Group value, an indirect reference resolved; null when the page has none.</summary>
    public string? GroupValue { get; init; }
}

/// <summary>The structural check of a file: the exit code of "qpdf --check" and its warning lines.</summary>
internal sealed record CheckRecord(int ExitCode, int WarningLines, string FirstLines);

/// <summary>The reference built from a pass-1 PDF by the page-box rule: every check of the construction and its outcome.</summary>
internal sealed record RoundedReference(
    bool Pass,
    string? Failure,
    int Pages,
    int MediaBoxKeys,
    int BoxArrays,
    int DifferingBytes,
    bool DifferencesInsideBoxNumbers,
    long Bytes,
    long CopyBytes,
    string SourceCheck,
    string CopyCheck,
    string? CopySha256);

/// <summary>
/// qpdf as the checker's reader of the PDF structure, never PDFsharp (the code
/// under test) and never pdfinfo (it rounds boxes to two decimals). Implements the
/// page-box rule of the assembly experiment (page boxes read as written, compared
/// in decimal arithmetic; the rounded reference built from the source bytes by a
/// same-length, in-place substitution of the box numbers, by no PDF writer).
/// </summary>
internal static partial class Qpdf
{
    public const string SourceBoxText = "0 0 594.95996 841.91998";
    public const string RoundedBoxText = "0 0 594.96 841.92";
    public const string Source = "source";
    public const string Rounded = "rounded";
    public const string Differs = "box-differs";

    public static readonly decimal[] SourceBox = [0m, 0m, 594.95996m, 841.91998m];
    public static readonly decimal[] RoundedBox = [0m, 0m, 594.96m, 841.92m];

    private const string SourceWidth = "594.95996";
    private const string SourceHeight = "841.91998";
    private const string RoundedWidth = "594.96000";
    private const string RoundedHeight = "841.92000";

    public static string Classify(decimal[] box) =>
        Equal(box, SourceBox) ? Source : Equal(box, RoundedBox) ? Rounded : Differs;

    public static bool Equal(decimal[] a, decimal[] b) => a.Length == b.Length && a.Zip(b).All(p => p.First == p.Second);

    public static async Task<string> VersionAsync(CancellationToken cancellation)
    {
        var result = await Tool.RunAsync("qpdf", ["--version"], TimeSpan.FromSeconds(30), cancellation);
        return result.StandardOutput.Split('\n')[0].Trim();
    }

    /// <summary>Every page of a PDF from qpdf's JSON (version 2): boxes inherited from the page tree when absent.</summary>
    public static async Task<List<QpdfPage>> PagesAsync(string pdf, string workDirectory, TimeSpan timeout, CancellationToken cancellation)
    {
        Directory.CreateDirectory(workDirectory);
        var jsonPath = Path.Combine(workDirectory, $"qpdf-{Guid.NewGuid():N}.json");
        try
        {
            var result = await Tool.RunAsync("qpdf", ["--json=2", "--json-key=pages", "--json-key=qpdf", pdf], timeout, cancellation, jsonPath);
            if (result.ExitCode is not (0 or 3))
            {
                throw new InvalidDataException($"qpdf --json could not read {Path.GetFileName(pdf)}: exit {result.ExitCode} {result.StandardError.Trim()}");
            }

            await using var stream = File.OpenRead(jsonPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
            var root = document.RootElement;
            var objects = root.GetProperty("qpdf")[1];
            var pages = new List<QpdfPage>();
            var number = 0;
            foreach (var page in root.GetProperty("pages").EnumerateArray())
            {
                number++;
                var reference = page.GetProperty("object").GetString() ?? string.Empty;
                var dictionary = Resolve(objects, reference);
                var media = Inherited(objects, reference, "/MediaBox");
                var crop = Inherited(objects, reference, "/CropBox");
                var annotations = new List<(string?, string?, string?, string?)>();
                if (dictionary.ValueKind == JsonValueKind.Object && dictionary.TryGetProperty("/Annots", out var annots))
                {
                    var array = Deref(objects, annots);
                    if (array.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in array.EnumerateArray())
                        {
                            var annotation = Deref(objects, item);
                            if (annotation.ValueKind != JsonValueKind.Object)
                            {
                                annotations.Add((null, null, null, null));
                                continue;
                            }

                            var subtype = annotation.TryGetProperty("/Subtype", out var s) ? s.GetString() : null;
                            string? action = null;
                            string? uri = null;
                            if (annotation.TryGetProperty("/A", out var a) && Deref(objects, a) is { ValueKind: JsonValueKind.Object } actionDictionary)
                            {
                                action = actionDictionary.TryGetProperty("/S", out var kind) ? kind.GetString() : null;
                                uri = actionDictionary.TryGetProperty("/URI", out var address) ? Text(Deref(objects, address)) : null;
                            }

                            var rect = annotation.TryGetProperty("/Rect", out var r) ? NumbersText(objects, Deref(objects, r)) : null;
                            annotations.Add((subtype, action, uri, rect));
                        }
                    }
                }

                var contents = page.TryGetProperty("contents", out var c) && c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList()
                    : new List<string>();
                var keys = new SortedDictionary<string, string>(StringComparer.Ordinal);
                if (dictionary.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in dictionary.EnumerateObject())
                    {
                        keys[property.Name] = Shape(property.Value);
                    }
                }

                pages.Add(new QpdfPage
                {
                    Page = number,
                    Object = reference,
                    MediaBox = media is null ? null : Box(objects, media.Value),
                    CropBox = crop is null ? null : Box(objects, crop.Value),
                    Annotations = annotations,
                    Contents = contents,
                    Keys = keys,
                    GroupValue = dictionary.ValueKind == JsonValueKind.Object && dictionary.TryGetProperty("/Group", out var group) ? Shape(Deref(objects, group)) : null,
                });
            }

            return pages;
        }
        finally
        {
            File.Delete(jsonPath);
        }
    }

    /// <summary>Runs "qpdf --check" and keeps its exit code, warning count and first lines.</summary>
    public static async Task<CheckRecord> CheckAsync(string pdf, TimeSpan timeout, CancellationToken cancellation)
    {
        var result = await Tool.RunAsync("qpdf", ["--check", pdf], timeout, cancellation);
        var lines = (result.StandardOutput + "\n" + result.StandardError).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("checking ", StringComparison.Ordinal))
            .ToList();
        var warnings = lines.Count(l => l.StartsWith("WARNING", StringComparison.Ordinal) || l.Contains(": WARNING", StringComparison.Ordinal));
        return new CheckRecord(result.ExitCode, warnings, string.Join(" | ", lines.Take(4)));
    }

    /// <summary>
    /// Builds the rounded reference of a pass-1 PDF: a copy with every page box's
    /// numbers substituted in place (594.95996 → 594.96000, 841.91998 → 841.92000),
    /// checked — one /MediaBox per page, every source box exactly the expected text,
    /// exactly 8 differing bytes per page, all inside the box numbers, the same size,
    /// the same structural-check result, and qpdf reading the rounded box on every page.
    /// The copy is kept (for rendering) only when every check holds.
    /// </summary>
    public static async Task<RoundedReference> BuildRoundedAsync(string source, string copyPath, string workDirectory, TimeSpan timeout, CancellationToken cancellation)
    {
        var bytes = await File.ReadAllBytesAsync(source, cancellation);
        var latin = Encoding.Latin1.GetString(bytes);
        var sourcePages = await PagesAsync(source, workDirectory, timeout, cancellation);
        var pages = sourcePages.Count;
        var keyCount = MediaBoxKey().Count(latin);
        var arrays = MediaBoxArray().Matches(latin);
        var sourceCheck = await CheckAsync(source, timeout, cancellation);
        var sourceCheckText = CheckText(sourceCheck);

        RoundedReference Failed(string reason, int differing = 0, bool inside = false, long copyBytes = 0, string copyCheck = "")
        {
            File.Delete(copyPath);
            return new RoundedReference(false, reason, pages, keyCount, arrays.Count, differing, inside, bytes.LongLength, copyBytes, sourceCheckText, copyCheck, null);
        }

        if (pages == 0 || keyCount != pages || arrays.Count != pages)
        {
            return Failed($"{keyCount} /MediaBox keys and {arrays.Count} box arrays for {pages} pages");
        }

        var texts = arrays.Select(m => Spaces().Replace(m.Groups[1].Value.Trim(), " ")).ToList();
        if (texts.FirstOrDefault(t => t != SourceBoxText) is { } other)
        {
            return Failed($"a source box text is '[{other}]', expected '[{SourceBoxText}]'");
        }

        if (sourcePages.Any(p => p.MediaBox is null || !Equal(p.MediaBox.Numbers, SourceBox)))
        {
            return Failed("qpdf does not read the source box on every page");
        }

        var copy = (byte[])bytes.Clone();
        var spans = new List<(int Start, int Length)>();
        foreach (Match match in arrays)
        {
            var inner = match.Groups[1];
            foreach (var (from, to) in new[] { (SourceWidth, RoundedWidth), (SourceHeight, RoundedHeight) })
            {
                var at = latin.IndexOf(from, inner.Index, inner.Length, StringComparison.Ordinal);
                if (at < 0)
                {
                    return Failed($"box number {from} not found inside a box array");
                }

                Encoding.Latin1.GetBytes(to).CopyTo(copy, at);
                spans.Add((at, from.Length));
            }
        }

        var differing = 0;
        var inside = true;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != copy[i])
            {
                differing++;
                inside &= spans.Any(s => i >= s.Start && i < s.Start + s.Length);
            }
        }

        await File.WriteAllBytesAsync(copyPath, copy, cancellation);
        var copyBytes = new FileInfo(copyPath).Length;
        var copyCheck = await CheckAsync(copyPath, timeout, cancellation);
        var copyCheckText = CheckText(copyCheck);
        if (differing != 8 * pages || !inside)
        {
            return Failed(string.Create(CultureInfo.InvariantCulture, $"{differing} differing bytes for {pages} pages (expected {8 * pages}), all inside the box numbers: {inside}"), differing, inside, copyBytes, copyCheckText);
        }

        if (copyBytes != bytes.LongLength)
        {
            return Failed(string.Create(CultureInfo.InvariantCulture, $"size changed from {bytes.LongLength} to {copyBytes}"), differing, inside, copyBytes, copyCheckText);
        }

        if (copyCheck.ExitCode != sourceCheck.ExitCode || copyCheck.WarningLines != sourceCheck.WarningLines)
        {
            return Failed($"structural check changed from {sourceCheckText} to {copyCheckText}", differing, inside, copyBytes, copyCheckText);
        }

        var copyPages = await PagesAsync(copyPath, workDirectory, timeout, cancellation);
        if (copyPages.Count != pages || copyPages.Any(p => p.MediaBox is null || !Equal(p.MediaBox.Numbers, RoundedBox)))
        {
            return Failed("qpdf does not read the rounded box on every page of the copy", differing, inside, copyBytes, copyCheckText);
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(copy));
        return new RoundedReference(true, null, pages, keyCount, arrays.Count, differing, inside, bytes.LongLength, copyBytes, sourceCheckText, copyCheckText, sha);
    }

    public static string CheckText(CheckRecord check) =>
        string.Create(CultureInfo.InvariantCulture, $"exit {check.ExitCode}, {check.WarningLines} warnings");

    /// <summary>The text of a qpdf JSON string value: "u:" text strings as they are, "b:" byte strings decoded from hex as Latin-1.</summary>
    public static string? Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString() ?? string.Empty;
        if (text.StartsWith("u:", StringComparison.Ordinal))
        {
            return text[2..];
        }

        if (text.StartsWith("b:", StringComparison.Ordinal))
        {
            try
            {
                return Encoding.Latin1.GetString(Convert.FromHexString(text[2..]));
            }
            catch (FormatException)
            {
                return text;
            }
        }

        return text;
    }

    private static string? NumbersText(JsonElement objects, JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? string.Join(' ', array.EnumerateArray().Select(e => Deref(objects, e).GetRawText())) : null;

    private static BoxValue Box(JsonElement objects, JsonElement box)
    {
        box = Deref(objects, box);
        if (box.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"a page box is not an array: {box.GetRawText()}");
        }

        var raw = new List<string>();
        foreach (var element in box.EnumerateArray())
        {
            var item = Deref(objects, element);
            if (item.ValueKind != JsonValueKind.Number)
            {
                throw new InvalidDataException($"a page box holds a value that is not a number: {item.GetRawText()}");
            }

            raw.Add(item.GetRawText());
        }

        return new BoxValue(string.Join(' ', raw), [.. raw.Select(r => decimal.Parse(r, NumberStyles.Float, CultureInfo.InvariantCulture))]);
    }

    private static JsonElement? Inherited(JsonElement objects, string reference, string key)
    {
        string? current = reference;
        for (var depth = 0; current is not null && depth < 64; depth++)
        {
            var value = Resolve(objects, current);
            if (value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (value.TryGetProperty(key, out var found))
            {
                return found;
            }

            current = value.TryGetProperty("/Parent", out var parent) && parent.ValueKind == JsonValueKind.String ? parent.GetString() : null;
        }

        return null;
    }

    private static JsonElement Resolve(JsonElement objects, string reference) =>
        objects.TryGetProperty("obj:" + reference, out var entry) && entry.TryGetProperty("value", out var value) ? value : default;

    private static JsonElement Deref(JsonElement objects, JsonElement value) =>
        value.ValueKind == JsonValueKind.String && Indirect().IsMatch(value.GetString() ?? string.Empty) ? Resolve(objects, value.GetString()!) : value;

    /// <summary>The JSON text of a value with every indirect reference replaced by "R", so renumbered objects compare equal.</summary>
    private static string Shape(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when Indirect().IsMatch(value.GetString() ?? string.Empty) => "R",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Shape)) + "]",
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + ":" + Shape(p.Value))) + "}",
        _ => value.GetRawText(),
    };

    [GeneratedRegex(@"^\d+ \d+ R$")]
    private static partial Regex Indirect();

    [GeneratedRegex(@"/MediaBox(?![A-Za-z0-9])")]
    private static partial Regex MediaBoxKey();

    [GeneratedRegex(@"/MediaBox\s*\[([^\]]*)\]")]
    private static partial Regex MediaBoxArray();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
