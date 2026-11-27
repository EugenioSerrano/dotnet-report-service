using System.Text;
using ReportService.TemplatePackages.Manifest;
using ReportService.Templates;

namespace ReportService.Samples.TemplateAuthoring;

/// <summary>
/// Writes a complete package for one engine from the tool's embedded scaffold set:
/// the manifest, the schema, a deterministic sample, the three HTML parts in the
/// engine's syntax and the stylesheet. The engine version is the one this build
/// runs, never a constant, so a scaffolded package's declaration never drifts from
/// the adapter that renders it. Every file is written as UTF-8 without a byte order
/// mark with LF line endings.
/// </summary>
internal static class ScaffoldWriter
{
    /// <summary>The manifest name of the tool's first engine.</summary>
    public const string ScribanName = TemplateEngineNames.Scriban;

    /// <summary>The manifest name of the tool's second engine.</summary>
    public const string HandlebarsName = TemplateEngineNames.Handlebars;

    /// <summary>The manifest name of the tool's third engine.</summary>
    public const string LiquidName = TemplateEngineNames.Liquid;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Reads an engine name without regard to case.</summary>
    /// <param name="name">The name as the caller wrote it.</param>
    /// <param name="engine">The engine, when the name is one of the three.</param>
    public static bool TryParseEngine(string? name, out TemplateEngine engine)
    {
        if (name is not null)
        {
            foreach (var candidate in TemplateEngineAdapters.All())
            {
                if (string.Equals(name, candidate.Engine, StringComparison.OrdinalIgnoreCase)
                    && TemplateEngineNames.TryParse(candidate.Engine, out var parsed))
                {
                    engine = parsed.Value;
                    return true;
                }
            }
        }

        engine = default;
        return false;
    }

    /// <summary>The three manifest names, for a usage message.</summary>
    public static string EngineNames => ScribanName + ", " + HandlebarsName + ", " + LiquidName;

    /// <summary>The part extension one engine's files carry.</summary>
    /// <param name="engine">One of the three engines.</param>
    public static string ExtensionOf(TemplateEngine engine) => engine switch
    {
        TemplateEngine.Scriban => PartExtensions.Scriban,
        TemplateEngine.Handlebars => PartExtensions.Handlebars,
        _ => PartExtensions.Liquid,
    };

    /// <summary>Writes the package of <paramref name="engine"/> into <paramref name="packageDirectory"/>.</summary>
    /// <param name="packageDirectory">The package directory, already confirmed to hold no file.</param>
    /// <param name="templateId">The template identifier the manifest declares.</param>
    /// <param name="version">The version the manifest declares.</param>
    /// <param name="engine">The engine the manifest declares.</param>
    /// <param name="engineVersion">The version of the engine this build runs.</param>
    public static void Write(
        string packageDirectory,
        string templateId,
        string version,
        TemplateEngine engine,
        string engineVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineVersion);

        Directory.CreateDirectory(packageDirectory);
        var html = Path.Combine(packageDirectory, "html");
        Directory.CreateDirectory(html);

        var extension = ExtensionOf(engine);
        var name = TemplateEngineNames.ToManifestName(engine);
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__TEMPLATE_ID__"] = templateId,
            ["__VERSION__"] = version,
            ["__ENGINE__"] = name,
            ["__ENGINE_VERSION__"] = engineVersion,
            ["__PART_EXTENSION__"] = extension,
        };

        WriteText(Path.Combine(packageDirectory, "template.yaml"), Substitute(Resource("Scaffolds/template.yaml"), tokens));
        WriteText(Path.Combine(packageDirectory, "schema.json"), Substitute(Resource("Scaffolds/schema.json"), tokens));
        WriteText(Path.Combine(packageDirectory, "sample.json"), Substitute(Resource("Scaffolds/sample.json"), tokens));
        WriteText(Path.Combine(html, "styles.css"), Resource("Scaffolds/styles.css"));
        WriteText(
            Path.Combine(html, "document-start.html" + extension),
            Resource("Scaffolds/parts/" + name + "/document-start" + extension));
        WriteText(
            Path.Combine(html, "row.html" + extension),
            Resource("Scaffolds/parts/" + name + "/row" + extension));
        WriteText(
            Path.Combine(html, "document-end.html" + extension),
            Resource("Scaffolds/parts/" + name + "/document-end" + extension));
    }

    private static string Substitute(string text, IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var (token, value) in tokens)
        {
            text = text.Replace(token, value, StringComparison.Ordinal);
        }

        return text;
    }

    private static string Resource(string logicalName)
    {
        using var stream = typeof(ScaffoldWriter).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"The embedded scaffold resource '{logicalName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void WriteText(string path, string text) => File.WriteAllText(path, text, Utf8NoBom);
}
