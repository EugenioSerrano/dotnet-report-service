using System.Text.Json;

namespace ReportService.Testing;

/// <summary>One pinned container image: repository, tag and the reference used to start it.</summary>
public sealed record PinnedImage(string Image, string Tag)
{
    /// <summary>The image reference, <c>image:tag</c>.</summary>
    public string Reference => $"{Image}:{Tag}";
}

/// <summary>
/// The container images the local composition starts for its dependencies,
/// read from the same file the composition reads, so the tests start exactly
/// the versions the composition runs.
/// </summary>
public sealed record CompositionPins(PinnedImage Database, PinnedImage Cache, PinnedImage WorkflowEngine)
{
    /// <summary>The pins file, relative to the repository root.</summary>
    public static readonly string RelativePath = Path.Combine("src", "ReportService.AppHost", "composition-pins.json");

    /// <summary>Reads the pins of the repository this test runs from.</summary>
    public static CompositionPins Load() => Read(Path.Combine(RepositoryPaths.Root, RelativePath));

    /// <summary>Reads a pins file; fails with a message naming the file when it is missing or incomplete.</summary>
    public static CompositionPins Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
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
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new InvalidOperationException($"The composition pins file {path} has no value for '{name}.{field}'.");
}
