using System.Text;

namespace ReportService.Testing.TemplateSource;

/// <summary>
/// The packages the integration classes seed the template source with. Every
/// package a class <b>takes</b> is one the product's own rules accept — the
/// schema, the sample and the parts below pass the take's static checks, so a
/// class that takes it is testing the take, not the checks. The refused ones exist
/// to be refused by the take's structural rules, before anything is written.
/// </summary>
public static class TemplatePackageFixtures
{
    /// <summary>A valid package of one version, with the manifest the owner reads.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version the folder and the manifest declare.</param>
    /// <param name="owner">The consumer the manifest names.</param>
    /// <param name="shared">Whether every consumer may use it.</param>
    /// <param name="engine">The engine the manifest declares.</param>
    /// <param name="title">A title the manifest carries, so two versions read differently.</param>
    public static Dictionary<string, byte[]> Valid(
        string templateId,
        string version,
        string owner = "billing-system",
        bool shared = false,
        string engine = "scriban",
        string title = "Invoice") =>
        new(StringComparer.Ordinal)
        {
            ["template.yaml"] = Encoding.UTF8.GetBytes(Manifest(templateId, version, owner, shared, engine, title)),
            ["schema.json"] = Encoding.UTF8.GetBytes(Schema),
            ["sample.json"] = Encoding.UTF8.GetBytes(Sample),
            ["html/document-start.html.sbn"] = Encoding.UTF8.GetBytes("<html><head><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body><header><h1>{{ header.title | html.escape }}</h1></header>"),
            ["html/row.html.sbn"] = Encoding.UTF8.GetBytes("<tr><td>{{ item.quantity | html.escape }}</td></tr>"),
            ["html/document-end.html.sbn"] = Encoding.UTF8.GetBytes("</body></html>"),
            ["html/styles.css"] = Encoding.UTF8.GetBytes("body { font-family: \"Liberation Sans\", sans-serif; }"),
        };

    /// <summary>A valid package whose manifest declares a version other than its folder.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="folderVersion">The folder's name.</param>
    /// <param name="declaredVersion">The version the manifest declares.</param>
    public static Dictionary<string, byte[]> FolderDoesNotMatchTheVersion(string templateId, string folderVersion, string declaredVersion)
    {
        var package = Valid(templateId, folderVersion);
        package["template.yaml"] = Encoding.UTF8.GetBytes(Manifest(templateId, declaredVersion, "billing-system", false, "scriban", "Invoice"));
        return package;
    }

    /// <summary>A package whose manifest declares an engine this deployment does not render.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    public static Dictionary<string, byte[]> UnsupportedEngine(string templateId, string version) =>
        Valid(templateId, version, engine: "razor");

    /// <summary>A package whose declared schema is not a file it holds.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    public static Dictionary<string, byte[]> SchemaMissingFromThePackage(string templateId, string version)
    {
        var package = Valid(templateId, version);
        package.Remove("schema.json");
        return package;
    }

    /// <summary>A package whose manifest names no owner.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    public static Dictionary<string, byte[]> OwnerMissing(string templateId, string version)
    {
        var package = Valid(templateId, version);
        package["template.yaml"] = Encoding.UTF8.GetBytes(
            Manifest(templateId, version, "billing-system", false, "scriban", "Invoice")
                .Replace("owner: billing-system\n", string.Empty, StringComparison.Ordinal));
        return package;
    }

    /// <summary>A valid package whose schema is outside the template profile.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="owner">The consumer the manifest names.</param>
    public static Dictionary<string, byte[]> SchemaOutsideTheProfile(string templateId, string version, string owner = "billing-system")
    {
        var package = Valid(templateId, version, owner);
        package["schema.json"] = Encoding.UTF8.GetBytes("""{ "$schema": "http://json-schema.org/draft-07/schema#", "type": "object" }""");
        return package;
    }

    /// <summary>A valid package whose sample breaks its schema.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="owner">The consumer the manifest names.</param>
    public static Dictionary<string, byte[]> SampleThatBreaksTheSchema(string templateId, string version, string owner = "billing-system")
    {
        var package = Valid(templateId, version, owner);
        package["sample.json"] = Encoding.UTF8.GetBytes(
            """{ "contractVersion": "1.0", "header": { "title": "Invoice" }, "items": [ { "quantity": "two" } ] }""");
        return package;
    }

    /// <summary>A valid package with a data output that is not escaped.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="owner">The consumer the manifest names.</param>
    public static Dictionary<string, byte[]> UnescapedOutput(string templateId, string version, string owner = "billing-system")
    {
        var package = Valid(templateId, version, owner);
        package["html/row.html.sbn"] = Encoding.UTF8.GetBytes("<tr><td>{{ item.quantity }}</td></tr>");
        return package;
    }

    /// <summary>A valid package whose PDF page setting is not one the service prints.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="owner">The consumer the manifest names.</param>
    public static Dictionary<string, byte[]> UnusablePageSetting(string templateId, string version, string owner = "billing-system")
    {
        var package = Valid(templateId, version, owner);
        package["template.yaml"] = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(package["template.yaml"]).Replace("paper: A4", "paper: A5", StringComparison.Ordinal));
        return package;
    }

    /// <summary>A valid package whose part includes a file the render does not load.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="owner">The consumer the manifest names.</param>
    public static Dictionary<string, byte[]> IncludeThatDoesNotResolve(string templateId, string version, string owner = "billing-system")
    {
        var package = Valid(templateId, version, owner);
        package["html/row.html.sbn"] = Encoding.UTF8.GetBytes(
            """<tr><td>{{ include "partials/missing.sbn" }}</td></tr>""");
        return package;
    }

    /// <summary>A valid package whose part refers to an asset the package does not hold.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="owner">The consumer the manifest names.</param>
    public static Dictionary<string, byte[]> MissingAsset(string templateId, string version, string owner = "billing-system")
    {
        var package = Valid(templateId, version, owner);
        package["html/row.html.sbn"] = Encoding.UTF8.GetBytes("""<tr><td><img src="logo.png"></td></tr>""");
        return package;
    }

    /// <summary>A valid package whose manifest declares a feature this increment does not deliver.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="owner">The consumer the manifest names.</param>
    public static Dictionary<string, byte[]> FeatureDeclaration(string templateId, string version, string owner = "billing-system")
    {
        var package = Valid(templateId, version, owner);
        var manifest = Encoding.UTF8.GetString(package["template.yaml"])
            + "\naggregates:\n  - name: total\n";
        package["template.yaml"] = Encoding.UTF8.GetBytes(manifest);
        return package;
    }

    private const string Schema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "contractVersion": { "type": "string" },
            "header": { "type": "object", "properties": { "title": { "type": "string" } }, "required": ["title"] },
            "items": { "type": "array", "items": { "type": "object", "properties": { "quantity": { "type": "integer" } }, "required": ["quantity"] } }
          },
          "required": ["contractVersion", "header", "items"]
        }
        """;

    private const string Sample = """
        { "contractVersion": "1.0", "header": { "title": "Invoice" }, "items": [ { "quantity": 1 } ] }
        """;

    private static string Manifest(
        string templateId,
        string version,
        string owner,
        bool shared,
        string engine,
        string title) =>
        $"""
        manifestVersion: "1.0"
        id: {templateId}
        version: "{version}"
        owner: {owner}
        shared: {(shared ? "true" : "false")}
        engine: {engine}
        engineVersion: "7.5.1"
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
          pdf:
            paper: A4
            orientation: portrait
            marginMm:
              top: 15
              right: 12
              bottom: 18
              left: 12
            verification:
              anchors:
                header: ["$.title"]
                item: ["$.quantity"]
        title: "{title}"
        """;
}
