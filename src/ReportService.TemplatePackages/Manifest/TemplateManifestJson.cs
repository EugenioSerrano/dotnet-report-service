using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ReportService.TemplatePackages.Manifest;

/// <summary>
/// The stored form of a manifest and its typed view. The text this writer
/// produces holds the document's members in the document's order without
/// indentation, so what it writes depends on what the manifest's author wrote and
/// never on a runtime's formatting or on a mapping's iteration order. The column
/// that keeps it may store the same document in another member order and
/// spacing, and a reader parses the document, never the text.
/// </summary>
public static class TemplateManifestJson
{
    private static readonly JsonSerializerOptions Canonical = new()
    {
        WriteIndented = false,
    };

    /// <summary>The canonical text of a manifest document.</summary>
    /// <param name="document">The document, as a JSON tree.</param>
    public static string Write(JsonNode? document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.ToJsonString(Canonical);
    }

    /// <summary>Reads the canonical text back into a JSON tree.</summary>
    /// <param name="canonicalJson">A stored manifest.</param>
    public static JsonNode Parse(string canonicalJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(canonicalJson);
        return JsonNode.Parse(canonicalJson) ?? throw new JsonException("the manifest is empty");
    }

    /// <summary>The typed view of a stored manifest.</summary>
    /// <param name="canonicalJson">A stored manifest.</param>
    /// <exception cref="JsonException">The document is not a manifest this build reads, or declares an engine it cannot render.</exception>
    public static TemplateManifest ReadTyped(string canonicalJson) =>
        JsonSerializer.Deserialize(canonicalJson, TemplateManifestJsonContext.Default.TemplateManifest)
        ?? throw new JsonException("the manifest is empty");
}

/// <summary>The serializer context of the typed view, generated at build time.</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(TemplateManifest))]
internal sealed partial class TemplateManifestJsonContext : JsonSerializerContext;
