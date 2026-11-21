using System.Text;

namespace ReportService.Testing.TemplatePackages;

/// <summary>
/// The at-bound controls: one report written in test support whose PDF body is table
/// markup, whose record schema leaves one string position unbounded, whose manifest
/// declares that field as an item anchor, whose stylesheet names the family the report is
/// measured with and whose page totals are declared off, so the take admits it and the
/// at-bound gate has a position to run on. Every shape case is this control with its
/// record schema - and, where a case needs it, its sample - mutated; no shipped pilot is
/// edited, and no file of the fixture library changes.
/// <para>
/// The row prints the record's key and amount beside the measured text, and the document
/// starts a table whose column captions name those three cells: the printed page is then
/// readable by the independent judge as well as by the product's own verification, and a
/// row at the bound fragments across the pages as a table row does.
/// </para>
/// </summary>
public static class AtBoundFixtures
{
    /// <summary>The template's identifier, shared by every package here.</summary>
    public const string TemplateId = "invoice-at-bound";

    /// <summary>The version every package here declares.</summary>
    public const string Version = "1.0.0";

    /// <summary>The consumer the manifests name.</summary>
    public const string Owner = "billing-system";

    /// <summary>The header the packages here carry.</summary>
    public const string Header = """{ "title": "Monthly statement" }""";

    /// <summary>The record the control carries: a small, schema-valid record.</summary>
    public const string Record = """{ "sequence": 1, "description": "Consulting", "amount": 100.00 }""";

    /// <summary>The record schema the control declares: one unbounded string, the anchored field.</summary>
    public const string ControlRecordSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["sequence", "description", "amount"],
          "properties": {
            "sequence": { "type": "integer", "minimum": 1 },
            "description": { "type": "string" },
            "amount": { "type": "number" }
          }
        }
        """;

    /// <summary>The envelope schema around one record schema.</summary>
    /// <param name="recordSchema">The record's own subschema, as a JSON object.</param>
    public static string Schema(string recordSchema) => $$"""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "additionalProperties": false,
          "required": ["contractVersion", "header", "items"],
          "properties": {
            "contractVersion": { "const": "1.0" },
            "header": {
              "type": "object",
              "additionalProperties": false,
              "required": ["title"],
              "properties": { "title": { "type": "string" } }
            },
            "items": { "type": "array", "items": {{recordSchema}} }
          }
        }
        """;

    /// <summary>The sample document around the records a case carries.</summary>
    /// <param name="records">The records, as JSON objects; none gives the empty sample.</param>
    public static string Sample(params string[] records) =>
        """{ "contractVersion": "1.0", "header": """ + Header + ", \"items\": [" + string.Join(", ", records) + "] }";

    /// <summary>The manifest the control declares, with the anchored item field a case needs.</summary>
    /// <param name="itemAnchor">The field path the manifest declares as an item anchor, or null to declare none.</param>
    /// <param name="withPdf">Whether the version declares a PDF output.</param>
    public static string Manifest(string? itemAnchor = "$.description", bool withPdf = true)
    {
        var manifest = """
            manifestVersion: "1.0"
            id: invoice-at-bound
            version: "1.0.0"
            owner: billing-system
            shared: false
            engine: scriban
            engineVersion: "7.5.1"
            localeDefaults:
              culture: es-AR
              timeZone: America/Argentina/Buenos_Aires
            dataContract:
              version: "1.0"
              profile: header-items-v1
              schema: schema.json
            outputs:
              html:
                parts:
                  documentStart: html/document-start.html.sbn
                  row: html/row.html.sbn
                  documentEnd: html/document-end.html.sbn
                styles: [html/styles.css]
            """;

        if (!withPdf)
        {
            return manifest + "\n";
        }

        var pdf = """
              pdf:
                paper: A4
                orientation: portrait
                marginMm: { top: 15, right: 12, bottom: 18, left: 12 }
                chunkLayout: page-boundary
                pageNumbering: global
                pageTotals:
                  enabled: false
                verification:
                  anchors:
                    header: ["$.title"]
                    item: [__ANCHOR__]
            """;

        return manifest + "\n" + pdf.Replace(
            "__ANCHOR__",
            itemAnchor is null ? string.Empty : "\"" + itemAnchor + "\"",
            StringComparison.Ordinal) + "\n";
    }

    /// <summary>The package's files, by package-relative path.</summary>
    /// <param name="recordSchema">The record's own subschema.</param>
    /// <param name="records">The records the sample carries; none gives the empty sample.</param>
    /// <param name="itemAnchor">The anchored item field the manifest declares.</param>
    /// <param name="withPdf">Whether the version declares a PDF output.</param>
    public static Dictionary<string, byte[]> Package(
        string? recordSchema = null,
        string[]? records = null,
        string? itemAnchor = "$.description",
        bool withPdf = true)
    {
        var schema = recordSchema ?? ControlRecordSchema;
        var sample = records switch
        {
            null => Sample(Record),
            [] => Sample(),
            _ => Sample(records),
        };
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["template.yaml"] = Encoding.UTF8.GetBytes(Manifest(itemAnchor, withPdf)),
            ["schema.json"] = Encoding.UTF8.GetBytes(Schema(schema)),
            ["sample.json"] = Encoding.UTF8.GetBytes(sample),
            ["html/document-start.html.sbn"] = Encoding.UTF8.GetBytes(
                "<html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body>"
                + "<h1>{{ header.title | html.escape }}</h1>"
                + "<table class=\"detail\"><colgroup><col class=\"col-seq\"><col class=\"col-description\"><col class=\"col-amount\"></colgroup>"
                + "<thead><tr><th class=\"seq\">Seq</th><th class=\"description\">Description</th><th class=\"amount\">Amount</th></tr></thead><tbody>"),
            ["html/row.html.sbn"] = Encoding.UTF8.GetBytes(
                "<tr><td class=\"seq\">{{ item.sequence | html.escape }}</td><td class=\"cell\">{{ item.description | html.escape }}</td><td class=\"amount\">{{ item.amount | html.escape }}</td></tr>"),
            ["html/document-end.html.sbn"] = Encoding.UTF8.GetBytes("</tbody></table></body></html>"),
            ["html/styles.css"] = Encoding.UTF8.GetBytes(
                "body { margin: 0; color: #222; font-family: \"Liberation Sans\", sans-serif; font-size: 9pt; } "
                + "table.detail { width: 100%; border-collapse: collapse; table-layout: fixed; } "
                + "table.detail thead { display: table-header-group; } "
                + "table.detail col.col-seq { width: 18mm; } "
                + "table.detail col.col-description { width: 138mm; } "
                + "table.detail col.col-amount { width: 30mm; } "
                + "table.detail th, table.detail td { font-family: \"Liberation Sans\", sans-serif; padding: 2pt 4pt; text-align: left; vertical-align: top; } "),
        };
    }
}
