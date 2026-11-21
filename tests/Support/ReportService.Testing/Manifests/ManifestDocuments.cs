using System.Text.Json.Nodes;
using ReportService.TemplatePackages.Manifest;

namespace ReportService.Testing.Manifests;

/// <summary>
/// Whether two manifest texts are the same document. A manifest is a JSON
/// document; the column that keeps it reorders members and drops the spacing it
/// was written with, so what a case compares is the document - every member and
/// every value - and never the text's bytes.
/// </summary>
public static class ManifestDocuments
{
    /// <summary>
    /// True when both texts hold the same members with the same values, in any
    /// member order and with any spacing. A member or a value that is missing,
    /// added or changed makes it false.
    /// </summary>
    /// <param name="expected">The text the take recorded.</param>
    /// <param name="actual">The text the store read back.</param>
    /// <exception cref="System.Text.Json.JsonException">A text is not JSON.</exception>
    public static bool AreTheSameDocument(string expected, string actual) =>
        JsonNode.DeepEquals(TemplateManifestJson.Parse(expected), TemplateManifestJson.Parse(actual));
}
