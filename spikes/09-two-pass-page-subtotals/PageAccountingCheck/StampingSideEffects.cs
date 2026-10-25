using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PageAccountingCheck;

/// <summary>The structural check of one file: verdict (pass-clean, pass-exempt, fail), the reason of a failure, exit code and warning count.</summary>
internal sealed record StructureCheck(string Verdict, string? Reason, int ExitCode, int WarningLines);

/// <summary>Colour and transparency entries counted over every object of a PDF.</summary>
internal sealed record TransparencyCounts(int SoftMasks, int BlendModesNotNormal, int AlphaBelowOne, int ColourSpaces, int Patterns)
{
    public bool AnyTransparency => SoftMasks + BlendModesNotNormal + AlphaBelowOne > 0;

    public bool Any => AnyTransparency || ColourSpaces + Patterns > 0;
}

/// <summary>A binary RGB image (PPM, "P6", maximum value 255) as written by the renderer.</summary>
internal sealed class PpmImage
{
    private PpmImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Three bytes per pixel: red, green, blue.</summary>
    public byte[] Pixels { get; }

    public static PpmImage Load(string path)
    {
        var data = File.ReadAllBytes(path);
        var position = 0;
        var magic = Token(data, ref position);
        if (magic != "P6")
        {
            throw new InvalidDataException($"{path}: not a binary PPM ({magic}).");
        }

        var width = int.Parse(Token(data, ref position), CultureInfo.InvariantCulture);
        var height = int.Parse(Token(data, ref position), CultureInfo.InvariantCulture);
        var maxValue = int.Parse(Token(data, ref position), CultureInfo.InvariantCulture);
        if (maxValue != 255)
        {
            throw new InvalidDataException($"{path}: maximum value {maxValue}, expected 255.");
        }

        position++; // the single whitespace byte after the header
        var length = width * height * 3;
        if (data.Length - position < length)
        {
            throw new InvalidDataException($"{path}: truncated pixel data.");
        }

        return new PpmImage(width, height, data.AsSpan(position, length).ToArray());
    }

    private static string Token(byte[] data, ref int position)
    {
        while (position < data.Length)
        {
            if (data[position] == '#')
            {
                while (position < data.Length && data[position] != '\n')
                {
                    position++;
                }
            }
            else if (char.IsWhiteSpace((char)data[position]))
            {
                position++;
            }
            else
            {
                break;
            }
        }

        var token = new StringBuilder();
        while (position < data.Length && !char.IsWhiteSpace((char)data[position]))
        {
            token.Append((char)data[position]);
            position++;
        }

        return token.ToString();
    }
}

/// <summary>
/// The checks of what the stamping library changes besides the stamps:
/// - the structural check of a file by the approved rule of the assembly experiment
///   (spikes/03-pdf-assembly): <c>qpdf --check</c>, stdout and stderr apart, the one
///   exempt warning text compared ordinally and exactly;
/// - the exact difference of each page dictionary between the first-pass and the
///   stamped PDF;
/// - the colour and transparency entries of a PDF.
/// </summary>
internal static partial class StampingSideEffects
{
    public const string PassClean = "pass-clean";
    public const string PassExempt = "pass-exempt";
    public const string Fail = "fail";

    /// <summary>The one exempt warning text (the zlib message on the stamping library's Flate streams).</summary>
    public const string ExemptTail = "input stream is complete but output may still be valid";

    /// <summary>The only value the page transparency group may have.</summary>
    public const string AllowedGroup = "{/CS:\"/DeviceRGB\",/S:\"/Transparency\"}";

    private const string ClosingLine = "qpdf: operation succeeded with warnings";

    /// <summary>
    /// Classifies <c>qpdf --check &lt;path&gt;</c>:
    /// - exit 0 → pass-clean when no stdout or stderr line starts with "WARNING:" or "ERROR:"
    ///   (qpdf's exit-0 summary is not scanned for "error"); otherwise fail (unparseable);
    /// - exit 3 → pass-exempt only when (a) no line of stdout or stderr contains "error"
    ///   (ordinal, case-insensitive); (b) stdout's first line is exactly "checking &lt;path&gt;";
    ///   (c) stderr holds at least one "WARNING:" line and every one matches the
    ///   "(… offset N)" shape with the file equal to the path and the full tail equal to the
    ///   exempt text; (d) apart from those, stderr holds exactly one more line, the closing
    ///   line, and it is the last; and no stdout line starts with "WARNING:" or "ERROR:";
    /// - exit 3 otherwise, exit 2, any other exit, a timeout → fail.
    /// </summary>
    public static async Task<StructureCheck> CheckAsync(string path, TimeSpan timeout, CancellationToken cancellation)
    {
        ToolResult result;
        try
        {
            result = await Tool.RunAsync("qpdf", ["--check", path], timeout, cancellation);
        }
        catch (Exception e) when (e is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new StructureCheck(Fail, "unparseable: " + e.Message, -1, 0);
        }

        return Classify(path, result.ExitCode, result.StandardOutput, result.StandardError);
    }

    public static StructureCheck Classify(string path, int exitCode, string standardOutput, string standardError)
    {
        var stdout = Lines(standardOutput);
        var stderr = Lines(standardError);
        var warnings = stdout.Concat(stderr).Count(l => l.StartsWith("WARNING:", StringComparison.Ordinal));
        bool Marked(string l) => l.StartsWith("WARNING:", StringComparison.Ordinal) || l.StartsWith("ERROR:", StringComparison.Ordinal);
        switch (exitCode)
        {
            case 0:
                return stdout.Concat(stderr).Any(Marked)
                    ? new StructureCheck(Fail, "unparseable: exit 0 with a WARNING: or ERROR: line", 0, warnings)
                    : new StructureCheck(PassClean, null, 0, warnings);
            case 2:
                return new StructureCheck(Fail, "exit-2", 2, warnings);
            case 3:
                break;
            default:
                return new StructureCheck(Fail, $"unparseable: exit {exitCode}", exitCode, warnings);
        }

        if (stdout.Concat(stderr).Any(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)))
        {
            return new StructureCheck(Fail, "error-line", 3, warnings);
        }

        if (stdout.Any(l => l.StartsWith("WARNING:", StringComparison.Ordinal)))
        {
            return new StructureCheck(Fail, "other-warning: a WARNING: line on stdout", 3, warnings);
        }

        if (stdout.Count == 0 || !string.Equals(stdout[0], "checking " + path, StringComparison.Ordinal))
        {
            return new StructureCheck(Fail, "unparseable: no checking line", 3, warnings);
        }

        var warningLines = stderr.Where(l => l.StartsWith("WARNING:", StringComparison.Ordinal)).ToList();
        if (warningLines.Count == 0)
        {
            return new StructureCheck(Fail, "unparseable: exit 3 without a WARNING: line", 3, warnings);
        }

        foreach (var line in warningLines)
        {
            var match = WarningShape().Match(line);
            if (!match.Success || !string.Equals(match.Groups["file"].Value, path, StringComparison.Ordinal)
                || !string.Equals(match.Groups["tail"].Value, ExemptTail, StringComparison.Ordinal))
            {
                return new StructureCheck(Fail, "other-warning: " + (line.Length > 200 ? line[..200] : line), 3, warnings);
            }
        }

        var others = stderr.Where(l => !l.StartsWith("WARNING:", StringComparison.Ordinal)).ToList();
        if (others.Count != 1 || !string.Equals(others[0], ClosingLine, StringComparison.Ordinal) || !string.Equals(stderr[^1], ClosingLine, StringComparison.Ordinal))
        {
            return new StructureCheck(Fail, "unparseable: closing line missing, misplaced or another stderr line", 3, warnings);
        }

        return new StructureCheck(PassExempt, null, 3, warnings);
    }

    /// <summary>
    /// The exact difference of one page dictionary, first pass → stamped: "/Annots" removed,
    /// "/Contents", "/Resources" and "/MediaBox" may change (the box is judged by the page-box
    /// rule), "/Group" added with exactly the allowed value; every other key present on both
    /// sides with the same shape (literal values for "/Type" and "/Tabs"; an indirect reference
    /// such as "/Parent" counts as a reference to an object of the same kind, because the
    /// library renumbers objects). Returns the violations, empty when the difference is exact.
    /// </summary>
    public static List<string> KeyDifference(QpdfPage before, QpdfPage after)
    {
        var violations = new List<string>();
        var b = before.Keys;
        var a = after.Keys;
        if (!b.ContainsKey("/Annots"))
        {
            violations.Add("/Annots absent in the first pass");
        }

        if (a.ContainsKey("/Annots"))
        {
            violations.Add("/Annots kept");
        }

        if (b.ContainsKey("/Group"))
        {
            violations.Add($"/Group already present in the first pass: {b["/Group"]}");
        }

        if (!a.ContainsKey("/Group"))
        {
            violations.Add("/Group not added");
        }
        else if (!string.Equals(after.GroupValue, AllowedGroup, StringComparison.Ordinal))
        {
            violations.Add($"/Group value {after.GroupValue}");
        }

        foreach (var key in b.Keys.Union(a.Keys).Where(k => k is not ("/Annots" or "/Group")))
        {
            if (!b.ContainsKey(key) || !a.ContainsKey(key))
            {
                violations.Add($"{key} {(b.ContainsKey(key) ? "removed" : "added")}");
                continue;
            }

            if (key is "/Contents" or "/Resources" or "/MediaBox")
            {
                continue;
            }

            if (!string.Equals(b[key], a[key], StringComparison.Ordinal))
            {
                violations.Add($"{key} changed: {b[key]} → {a[key]}");
            }
        }

        return violations;
    }

    /// <summary>Counts, over every object of the PDF (qpdf's JSON), the soft masks, blend modes other than Normal, alpha below 1, colour-space and pattern entries.</summary>
    public static async Task<TransparencyCounts> CountAsync(string pdf, string workDirectory, TimeSpan timeout, CancellationToken cancellation)
    {
        Directory.CreateDirectory(workDirectory);
        var jsonPath = Path.Combine(workDirectory, $"qpdf-objects-{Guid.NewGuid():N}.json");
        try
        {
            var result = await Tool.RunAsync("qpdf", ["--json=2", "--json-key=qpdf", pdf], timeout, cancellation, jsonPath);
            if (result.ExitCode is not (0 or 3))
            {
                throw new InvalidDataException($"qpdf --json could not read {Path.GetFileName(pdf)}: exit {result.ExitCode} {result.StandardError.Trim()}");
            }

            await using var stream = File.OpenRead(jsonPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
            var counts = new int[5];
            foreach (var entry in document.RootElement.GetProperty("qpdf")[1].EnumerateObject())
            {
                Walk(entry.Value, counts);
            }

            return new TransparencyCounts(counts[0], counts[1], counts[2], counts[3], counts[4]);
        }
        finally
        {
            File.Delete(jsonPath);
        }
    }

    private static void Walk(JsonElement value, int[] counts)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "/SMask":
                            counts[0]++;
                            break;
                        case "/BM" when !IsNormal(property.Value):
                            counts[1]++;
                            break;
                        case "/ca" or "/CA" when property.Value.ValueKind == JsonValueKind.Number && property.Value.GetDouble() < 1:
                            counts[2]++;
                            break;
                        case "/ColorSpace":
                            counts[3]++;
                            break;
                        case "/Pattern":
                            counts[4]++;
                            break;
                        default:
                            break;
                    }

                    Walk(property.Value, counts);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    Walk(item, counts);
                }

                break;
            default:
                break;
        }
    }

    private static bool IsNormal(JsonElement value) =>
        (value.ValueKind == JsonValueKind.String && value.GetString() == "/Normal")
        || (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1 && value[0].ValueKind == JsonValueKind.String && value[0].GetString() == "/Normal");

    private static List<string> Lines(string text) =>
        [.. text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).Where(l => l.Length > 0)];

    [GeneratedRegex(@"^WARNING: (?<file>.+?) \((?:object \d+ \d+, )?offset (?<offset>\d+)\): (?<tail>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex WarningShape();
}
