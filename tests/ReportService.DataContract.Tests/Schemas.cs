using System.Text;
using ReportService.Testing;

namespace ReportService.DataContract.Tests;

/// <summary>
/// The schema and sample documents the tests use: the pilot templates' own
/// files, a small valid envelope the refusal cases are derived from, and
/// builders for the shapes whose size or nesting has to be computed.
/// </summary>
internal static class Schemas
{
    /// <summary>The dialect the profile requires.</summary>
    public const string Dialect = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>A small envelope schema that is inside the profile, with both roots.</summary>
    public const string ValidEnvelope =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "header": { "type": "object" },
            "items": { "type": "array", "items": { "type": "object" } }
          }
        }
        """;

    /// <summary>The dialects' schema document of a pilot template, from the repository.</summary>
    public static byte[] PilotSchema(string template) =>
        File.ReadAllBytes(Path.Combine(RepositoryPaths.Root, "samples", "templates", template, "schema.json"));

    /// <summary>The sample envelope of a pilot template, from the repository.</summary>
    public static byte[] PilotSample(string template) =>
        File.ReadAllBytes(Path.Combine(RepositoryPaths.Root, "samples", "templates", template, "sample.json"));

    /// <summary>Encodes a document as UTF-8 bytes.</summary>
    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>An envelope whose header root nests <paramref name="schemaDepth"/> schema positions deep, the root counted as one.</summary>
    public static byte[] WithHeaderDepth(int schemaDepth)
    {
        var chain = schemaDepth - 2;
        var text = new StringBuilder();
        text.Append("""{"$schema":"https://json-schema.org/draft/2020-12/schema","properties":{"header":""");
        for (var level = 1; level <= chain; level++)
        {
            text.Append("""{"properties":{"a":""");
        }

        text.Append("""{"type":"object"}""");
        for (var level = 1; level <= chain; level++)
        {
            text.Append("}}");
        }

        text.Append(""","items":{"items":{"type":"object"}}}}""");
        return Utf8(text.ToString());
    }

    /// <summary>An envelope schema with one more string member in the header, so a format case has a field.</summary>
    public static byte[] WithHeaderString(string? format, int? maxLength = null)
    {
        var rules = new List<string> { "\"type\":\"string\"" };
        if (format is not null)
        {
            rules.Add($"\"format\":\"{format}\"");
        }

        if (maxLength is not null)
        {
            rules.Add($"\"maxLength\":{maxLength}");
        }

        return Utf8(
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "properties": {
                "header": { "type": "object", "properties": { "value": {
            """
            + string.Join(",", rules)
            + """
                  } } },
                "items": { "type": "array", "items": { "type": "object" } }
              }
            }
            """);
    }

    /// <summary>A schema that pads to exactly <paramref name="bytes"/> bytes by widening its title.</summary>
    public static byte[] OfSize(int bytes)
    {
        const string prefix = "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"title\":\"x";
        const string suffix = "\",\"properties\":{\"header\":{\"type\":\"object\"},\"items\":{\"items\":{\"type\":\"object\"}}}}";
        var padding = bytes - Utf8(prefix + suffix).Length;
        Assert.True(padding >= 0);
        return Utf8(prefix + new string('x', padding) + suffix);
    }
}
