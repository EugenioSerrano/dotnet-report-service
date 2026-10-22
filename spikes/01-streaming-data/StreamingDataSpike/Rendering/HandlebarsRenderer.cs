using System.Globalization;
using HandlebarsDotNet;

namespace StreamingDataSpike.Rendering;

/// <summary>
/// Handlebars.Net: an isolated environment with the policy text encoder and
/// the invariant format provider (its default is the current culture); each
/// part compiled once (compiled templates are thread-safe); every render gets
/// a new root dictionary with the six context names.
/// </summary>
internal sealed class HandlebarsRenderer : IRecordRenderer
{
    private readonly IHandlebars _environment = Handlebars.Create(new HandlebarsConfiguration
    {
        TextEncoder = new HtmlPolicyHandlebarsEncoder(),
        FormatProvider = CultureInfo.InvariantCulture,
    });

    private readonly Dictionary<string, HandlebarsTemplate<TextWriter, object, object>> _templates = new(StringComparer.Ordinal);

    public string Engine => "handlebars";

    public void Prepare(TemplateParts parts)
    {
        foreach (var (name, (text, _)) in parts.Parts)
        {
            using var reader = new StringReader(text);
            _templates[name] = _environment.Compile(reader);
        }
    }

    public void Render(string part, RenderContext context, TextWriter output)
    {
        var root = context.ReuseEngineState && context.EngineState is Dictionary<string, object?> reused ? reused : new Dictionary<string, object?>(6, StringComparer.Ordinal);
        if (context.ReuseEngineState)
        {
            context.EngineState = root;
        }

        root["header"] = context.Header;
        root["item"] = context.Item;
        root["group"] = context.Group;
        root["block"] = context.Block;
        root["aggregates"] = context.Aggregates;
        root["render"] = context.Render;
        _templates[part](output, root, null!);
    }
}
