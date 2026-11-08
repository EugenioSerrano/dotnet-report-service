using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReportService.TemplatePackages.Packages;

namespace ReportService.TemplatePackages.Records;

/// <summary>
/// The stored form of the list of files a take verified: one JSON array, one
/// object per file, with the members <c>path</c>, <c>size</c> and <c>sha256</c>.
/// The writer orders the files by their path, so the same files always produce the
/// same document; the reader parses the document and refuses every shape the
/// writer could not have produced, naming the member and the shape it found.
/// </summary>
public static partial class TemplateSourceObjects
{
    /// <summary>The most files one taken version records, the record's own bound.</summary>
    public const int MaxCount = 256;

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
    };

    /// <summary>The canonical text of a list of verified files.</summary>
    /// <param name="objects">The files, in any order.</param>
    public static string Write(IReadOnlyList<TemplateSourceObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var document = new JsonArray();
        foreach (var item in objects.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(item);
            document.Add(new JsonObject
            {
                ["path"] = item.Path,
                ["size"] = item.Size,
                ["sha256"] = item.Sha256,
            });
        }

        return document.ToJsonString(Compact);
    }

    /// <summary>The files a stored document names, in the document's order.</summary>
    /// <param name="json">A stored list of verified files.</param>
    /// <exception cref="JsonException">The document is not a list of valid file entries.</exception>
    public static TemplateSourceObject[] Read(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        var document = JsonNode.Parse(json) as JsonArray
            ?? throw new JsonException("the source objects document is not a JSON array");
        if (document.Count > MaxCount)
        {
            throw new JsonException($"the source objects document holds more than {MaxCount} entries");
        }

        var objects = new List<TemplateSourceObject>(document.Count);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in document)
        {
            if (element is not JsonObject holder)
            {
                throw new JsonException("a source object is not a JSON object");
            }

            var members = holder.Select(member => member.Key).Order(StringComparer.Ordinal).ToArray();
            if (members.Length != 3 || !members.SequenceEqual(["path", "sha256", "size"], StringComparer.Ordinal))
            {
                throw new JsonException("a source object must carry exactly the members 'path', 'size' and 'sha256'");
            }

            var path = holder["path"];
            if (path is null || path.GetValueKind() != JsonValueKind.String)
            {
                throw new JsonException("the source object member 'path' is not a string");
            }

            var value = path.GetValue<string>();
            if (!PackagePath.IsValid(value, out var reason))
            {
                throw new JsonException($"the source object member 'path' is not a package path: {reason}");
            }

            if (!paths.Add(value))
            {
                throw new JsonException($"the source object member 'path' names '{value}' twice");
            }

            var size = holder["size"];
            if (size is null || size.GetValueKind() != JsonValueKind.Number)
            {
                throw new JsonException("the source object member 'size' is not a number");
            }

            long bytes;
            try
            {
                bytes = size.GetValue<long>();
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException)
            {
                throw new JsonException("the source object member 'size' is not an integer", exception);
            }

            if (bytes < 0)
            {
                throw new JsonException("the source object member 'size' is negative");
            }

            var hash = holder["sha256"];
            if (hash is null || hash.GetValueKind() != JsonValueKind.String)
            {
                throw new JsonException("the source object member 'sha256' is not a string");
            }

            var sha256 = hash.GetValue<string>();
            if (!Sha256Hex().IsMatch(sha256))
            {
                throw new JsonException("the source object member 'sha256' is not 64 lower-case hexadecimal characters");
            }

            objects.Add(new TemplateSourceObject(value, bytes, sha256));
        }

        return [.. objects];
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Hex();
}
