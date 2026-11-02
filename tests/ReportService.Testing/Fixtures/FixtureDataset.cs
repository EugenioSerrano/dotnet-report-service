using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReportService.Testing.Fixtures;

/// <summary>
/// The pilot dataset a fixture is generated from, loaded only after its SHA-256
/// matches the line of the sums file that names it: a fixture is never built
/// from a file whose content differs from the recorded one.
/// </summary>
public sealed class FixtureDataset
{
    private FixtureDataset(string fileName, string sha256, JsonObject header, IReadOnlyList<JsonObject> items)
    {
        FileName = fileName;
        Sha256 = sha256;
        Header = header;
        Items = items;
    }

    /// <summary>The file name of the dataset.</summary>
    public string FileName { get; }

    /// <summary>The SHA-256 of the dataset's bytes.</summary>
    public string Sha256 { get; }

    /// <summary>The dataset's header, as the data contract holds it.</summary>
    public JsonObject Header { get; }

    /// <summary>The dataset's detail records, in document order.</summary>
    public IReadOnlyList<JsonObject> Items { get; }

    /// <summary>
    /// Loads the dataset and checks its hash. A file without its sums line or
    /// with a different hash is refused before any fixture is generated.
    /// </summary>
    public static FixtureDataset Load(string datasetPath, string sumsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sumsPath);

        var bytes = File.ReadAllBytes(datasetPath);
        var fileName = Path.GetFileName(datasetPath);
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var expected = ReadSums(sumsPath).GetValueOrDefault(fileName)
            ?? throw new InvalidDataException($"The sums file holds no line for {fileName}.");
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The SHA-256 of {fileName} is {actual}, expected {expected}.");
        }

        var root = JsonNode.Parse(bytes) as JsonObject
            ?? throw new InvalidDataException($"{fileName} is not a JSON object.");
        var header = root["header"] as JsonObject
            ?? throw new InvalidDataException($"{fileName} holds no header object.");
        var items = root["items"] as JsonArray
            ?? throw new InvalidDataException($"{fileName} holds no items array.");

        return new FixtureDataset(
            fileName,
            actual,
            header,
            [.. items.Select(item => item as JsonObject
                ?? throw new InvalidDataException($"{fileName} holds a record that is not an object."))]);
    }

    /// <summary>Reads a sums file: one "hash  name" line per file.</summary>
    public static Dictionary<string, string> ReadSums(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                sums[parts[1].TrimStart('*')] = parts[0].ToLowerInvariant();
            }
        }

        return sums;
    }

    /// <summary>The SHA-256 of a file, lower-case hexadecimal.</summary>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
