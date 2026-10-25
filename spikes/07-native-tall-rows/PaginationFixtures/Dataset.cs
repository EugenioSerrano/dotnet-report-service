using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace PaginationFixtures;

/// <summary>One record of the grouped pilot dataset.</summary>
public sealed record DatasetRecord(int Sequence, string GroupId, string GroupName, string Date, string Description, decimal Amount)
{
    /// <summary>The amount as printed: invariant culture, exactly two decimals (the dataset's own text).</summary>
    public string AmountText => Amount.ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>
/// The grouped pilot dataset, loaded only after its SHA-256 matches the line of
/// the sums file that names it. Adapted from the earlier layout spike
/// (spikes/02-pdf-layout, BlockSource.cs).
/// </summary>
public sealed class Dataset
{
    private Dataset(string fileName, string sha256, IReadOnlyList<DatasetRecord> records)
    {
        FileName = fileName;
        Sha256 = sha256;
        Records = records;
    }

    public string FileName { get; }

    public string Sha256 { get; }

    public IReadOnlyList<DatasetRecord> Records { get; }

    /// <summary>Loads the dataset; a hash mismatch or a missing sums line throws before anything is generated.</summary>
    public static Dataset Load(string datasetPath, string sumsPath)
    {
        var fileName = Path.GetFileName(datasetPath);
        var bytes = File.ReadAllBytes(datasetPath);
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var expected = Sums.Read(sumsPath).GetValueOrDefault(fileName)
            ?? throw new InvalidDataException($"No line for {fileName} in {sumsPath}.");
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"SHA-256 of {fileName} is {actual}, expected {expected}.");
        }

        using var document = JsonDocument.Parse(bytes);
        var records = new List<DatasetRecord>();
        foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
        {
            records.Add(new DatasetRecord(
                item.GetProperty("sequence").GetInt32(),
                item.GetProperty("groupId").GetString() ?? string.Empty,
                item.GetProperty("groupName").GetString() ?? string.Empty,
                item.GetProperty("date").GetString() ?? string.Empty,
                item.GetProperty("description").GetString() ?? string.Empty,
                item.GetProperty("amount").GetDecimal()));
        }

        return new Dataset(fileName, actual, records);
    }
}

/// <summary>Reads and writes SHA256SUMS files ("hash  name" per line, names sorted ordinally).</summary>
public static class Sums
{
    public static Dictionary<string, string> Read(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                result[parts[1].TrimStart('*')] = parts[0].ToLowerInvariant();
            }
        }

        return result;
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Writes the sums of every *.json file of the folder, sorted by name, LF line ends.</summary>
    public static void WriteFolder(string directory, string sumsPath)
    {
        var lines = Directory.EnumerateFiles(directory, "*.json")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .Select(name => $"{HashFile(Path.Combine(directory, name!))}  {name}\n");
        File.WriteAllText(sumsPath, string.Concat(lines));
    }
}
