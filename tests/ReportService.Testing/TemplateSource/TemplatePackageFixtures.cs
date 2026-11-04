using System.Text;

namespace ReportService.Testing.TemplateSource;

/// <summary>
/// The packages the integration classes seed the template source with. Every
/// package a class <b>takes</b> is one the product's own rules accept: the checks
/// that refuse an invalid package are later changes, so an unchecked copy is never
/// frozen from a package written to be refused. The refused ones exist to be
/// refused — by the take's structural rules, before anything is written.
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
            ["schema.json"] = Encoding.UTF8.GetBytes("{}"),
            ["html/document-start.html.sbn"] = Encoding.UTF8.GetBytes("<html><body>"),
            ["html/row.html.sbn"] = Encoding.UTF8.GetBytes("<tr></tr>"),
            ["html/document-end.html.sbn"] = Encoding.UTF8.GetBytes("</body></html>"),
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
        title: "{title}"
        """;
}
