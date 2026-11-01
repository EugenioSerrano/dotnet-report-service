using System.Text.Json;

namespace ReportService.AppHost;

/// <summary>One pinned container image of the composition: repository and tag.</summary>
internal sealed record PinnedImage(string Image, string Tag);

/// <summary>
/// The container images the composition starts for its dependencies, read from
/// <c>composition-pins.json</c> next to this project. The same file is read by
/// the integration tests, so the composition and the tests start the same
/// versions.
/// </summary>
internal sealed record CompositionPins(PinnedImage Database, PinnedImage Cache, PinnedImage WorkflowEngine)
{
    public const string FileName = "composition-pins.json";

    /// <summary>Reads the pins; fails with a message naming the file when it is missing or incomplete.</summary>
    public static CompositionPins Read(string appHostDirectory)
    {
        var path = Path.Combine(appHostDirectory, FileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"The composition pins file {path} is missing.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"The composition pins file {path} is not valid JSON: {exception.Message}", exception);
        }

        using (document)
        {
            return new CompositionPins(
                ReadImage(document.RootElement, "database", path),
                ReadImage(document.RootElement, "cache", path),
                ReadImage(document.RootElement, "workflowEngine", path));
        }
    }

    private static PinnedImage ReadImage(JsonElement root, string name, string path)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out var entry)
            || entry.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"The composition pins file {path} has no '{name}' entry.");
        }

        return new PinnedImage(ReadText(entry, name, "image", path), ReadText(entry, name, "tag", path));
    }

    private static string ReadText(JsonElement entry, string name, string field, string path) =>
        entry.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new InvalidOperationException($"The composition pins file {path} has no value for '{name}.{field}'.");
}
