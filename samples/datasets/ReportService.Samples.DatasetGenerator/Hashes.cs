using System.Security.Cryptography;
using System.Text;

namespace ReportService.Samples.DatasetGenerator;

/// <summary>SHA-256 of files and the <c>SHA256SUMS</c> file format (<c>&lt;hex&gt;  &lt;file&gt;</c>).</summary>
internal static class Hashes
{
    /// <summary>Computes the SHA-256 of a file by streaming it; returns lowercase hexadecimal.</summary>
    public static string OfFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Reads a sums file into a map from file name to hash.</summary>
    public static Dictionary<string, string> ReadSums(string path)
    {
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new FormatException($"Malformed line in {path}: '{line}'");
            }

            sums[line[(separator + 2)..]] = line[..separator];
        }

        return sums;
    }

    /// <summary>Formats entries as a sums file, sorted by file name, one <c>\n</c>-terminated line each.</summary>
    public static string FormatSums(IEnumerable<KeyValuePair<string, string>> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries.OrderBy(static e => e.Key, StringComparer.Ordinal))
        {
            builder.Append(entry.Value).Append("  ").Append(entry.Key).Append('\n');
        }

        return builder.ToString();
    }
}
