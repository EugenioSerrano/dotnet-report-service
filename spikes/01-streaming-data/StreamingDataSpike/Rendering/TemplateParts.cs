namespace StreamingDataSpike.Rendering;

/// <summary>
/// The HTML parts of one template in one engine's syntax:
/// <c>document-start</c>, <c>row</c>, <c>document-end</c> and, for grouped
/// templates, <c>group-start</c> and <c>group-end</c>.
/// </summary>
internal sealed class TemplateParts
{
    /// <summary>Part names in rendering order.</summary>
    public static readonly string[] Names = ["document-start", "group-start", "row", "group-end", "document-end"];

    private TemplateParts(string templateId, Dictionary<string, (string Text, string Path)> parts, IReadOnlyList<string> corrected)
    {
        TemplateId = templateId;
        Parts = parts;
        Corrected = corrected;
    }

    /// <summary>The template id, for example <c>pilot-grouped</c>.</summary>
    public string TemplateId { get; }

    /// <summary>Part text and its relative location, by part name.</summary>
    public IReadOnlyDictionary<string, (string Text, string Path)> Parts { get; }

    /// <summary>Scriban parts taken from the corrected copies of this folder instead of the samples.</summary>
    public IReadOnlyList<string> Corrected { get; }

    /// <summary>Whether the template has group parts.</summary>
    public bool HasGroups => Parts.ContainsKey("group-start");

    /// <summary>
    /// Loads the parts of <paramref name="templateId"/> for <paramref name="engine"/>.
    /// Scriban parts come from the sample templates (read-only) unless a
    /// corrected copy exists in this folder's <c>templates/</c>; Handlebars and
    /// Liquid parts come from this folder's <c>templates/</c>.
    /// </summary>
    public static TemplateParts Load(string templateId, string engine)
    {
        var root = RepositoryPaths.Root;
        var spikeTemplates = Path.Combine(root, "spikes", "01-streaming-data", "templates", templateId, "html");
        var sampleTemplates = Path.Combine(root, "samples", "templates", templateId, "html");
        var extension = engine switch
        {
            "scriban" => ".html.sbn",
            "handlebars" => ".html.hbs",
            "liquid" => ".html.liquid",
            _ => throw new ArgumentException("Unknown engine " + engine, nameof(engine)),
        };

        var parts = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var corrected = new List<string>();
        foreach (var name in Names)
        {
            var spikePath = Path.Combine(spikeTemplates, name + extension);
            var path = spikePath;
            if (engine == "scriban")
            {
                if (File.Exists(spikePath))
                {
                    corrected.Add(name);
                }
                else
                {
                    path = Path.Combine(sampleTemplates, name + extension);
                }
            }

            if (File.Exists(path))
            {
                using var reader = new StreamReader(path, new System.Text.UTF8Encoding(false));
                parts[name] = (reader.ReadToEnd(), Path.GetRelativePath(root, path).Replace('\\', '/'));
            }
        }

        if (!parts.ContainsKey("document-start") || !parts.ContainsKey("row") || !parts.ContainsKey("document-end"))
        {
            throw new FileNotFoundException($"Template {templateId} has no complete set of {engine} parts.");
        }

        return new TemplateParts(templateId, parts, corrected);
    }
}
