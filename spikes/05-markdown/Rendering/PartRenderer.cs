using System.Globalization;
using Scriban;
using Scriban.Runtime;

namespace MarkdownSpike.Rendering;

/// <summary>The parts of a template package, in the order the document is produced.</summary>
internal enum PartKind
{
    DocumentStart,
    GroupStart,
    Row,
    GroupEnd,
    DocumentEnd,
}

/// <summary>
/// Loads a package's parts, parses each once, and renders them one at a time
/// with a single template context for the whole conversion run.
/// </summary>
internal sealed class PartRenderer
{
    private static readonly (PartKind Kind, string Name, bool Required)[] PartFiles =
    [
        (PartKind.DocumentStart, "document-start", true),
        (PartKind.GroupStart, "group-start", false),
        (PartKind.Row, "row", true),
        (PartKind.GroupEnd, "group-end", false),
        (PartKind.DocumentEnd, "document-end", true),
    ];

    private readonly Dictionary<PartKind, Template> templates;
    private readonly TemplateContext context;
    private readonly ScriptObject globals;

    private PartRenderer(Dictionary<PartKind, Template> templates, bool markdownFunctions)
    {
        this.templates = templates;
        HasGroupParts = templates.ContainsKey(PartKind.GroupStart) && templates.ContainsKey(PartKind.GroupEnd);
        globals = new ScriptObject();
        globals.SetValue("header", null, false);
        globals.SetValue("item", null, false);
        globals.SetValue("group", null, false);
        globals.SetValue("aggregates", null, false);
        if (markdownFunctions)
        {
            globals.Import("md_escape", new Func<object?, string>(value =>
                MarkdownText.Escape(Convert.ToString(value, CultureInfo.InvariantCulture))));
        }

        context = new TemplateContext { StrictVariables = true };
        context.PushCulture(CultureInfo.InvariantCulture);
        context.PushGlobal(globals);
    }

    public bool HasGroupParts { get; }

    /// <summary>Folder that holds the parts, e.g. "samples/templates/pilot-grouped/html".</summary>
    public string Folder { get; private init; } = string.Empty;

    /// <summary>Loads "&lt;part&gt;.&lt;extension&gt;.sbn" files; group parts are optional.</summary>
    public static PartRenderer Load(string folder, string extension, bool markdownFunctions)
    {
        var templates = new Dictionary<PartKind, Template>();
        foreach ((PartKind kind, string name, bool required) in PartFiles)
        {
            string path = Path.Combine(folder, $"{name}.{extension}.sbn");
            if (!File.Exists(path))
            {
                if (required)
                {
                    throw new FileNotFoundException($"Template part not found: {path}", path);
                }

                continue;
            }

            Template template = Template.Parse(File.ReadAllText(path), path);
            if (template.HasErrors)
            {
                throw new InvalidDataException($"Template part {path} has errors: {string.Join("; ", template.Messages)}");
            }

            templates[kind] = template;
        }

        return new PartRenderer(templates, markdownFunctions) { Folder = folder };
    }

    public void SetHeader(ScriptObject header) => globals.SetValue("header", header, false);

    public void SetItem(ScriptObject item) => globals.SetValue("item", item, false);

    public void SetGroup(string groupId, string groupName)
    {
        var group = new ScriptObject();
        group.SetValue("groupId", groupId, false);
        group.SetValue("groupName", groupName, false);
        globals.SetValue("group", group, false);
    }

    public void SetGroupAmount(decimal groupAmount)
    {
        var aggregates = new ScriptObject();
        aggregates.SetValue("groupAmount", groupAmount, false);
        globals.SetValue("aggregates", aggregates, false);
    }

    public string Render(PartKind kind) => templates[kind].Render(context);
}
