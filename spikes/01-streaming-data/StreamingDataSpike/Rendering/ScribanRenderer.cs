using Scriban;
using Scriban.Runtime;

namespace StreamingDataSpike.Rendering;

/// <summary>
/// Scriban: each part is parsed once; every render gets a new
/// <see cref="TemplateContext"/> (it is not thread-safe) whose globals hold
/// the six context names. Loop, recursion and string limits stay at their
/// defaults. Scriban never escapes by itself: the templates call
/// <c>html.escape</c>, which is the policy.
/// </summary>
internal sealed class ScribanRenderer : IRecordRenderer
{
    private readonly Dictionary<string, Template> _templates = new(StringComparer.Ordinal);

    public string Engine => "scriban";

    public void Prepare(TemplateParts parts)
    {
        foreach (var (name, (text, path)) in parts.Parts)
        {
            var template = Template.Parse(text, path);
            if (template.HasErrors)
            {
                throw new InvalidOperationException($"Scriban part {path} does not parse: {string.Join("; ", template.Messages)}");
            }

            _templates[name] = template;
        }
    }

    public void Render(string part, RenderContext context, TextWriter output)
    {
        var state = context.ReuseEngineState && context.EngineState is State reused ? reused : NewState();
        if (context.ReuseEngineState)
        {
            context.EngineState = state;
        }

        var globals = state.Globals;
        globals["header"] = context.Header;
        globals["item"] = context.Item;
        globals["group"] = context.Group;
        globals["block"] = context.Block;
        globals["aggregates"] = context.Aggregates;
        globals["render"] = context.Render;

        // Render (not Evaluate, which turns output off) into the pushed output;
        // the returned text is the forwarding writer's empty ToString.
        state.Context.PushOutput(new TextWriterOutput(new ForwardingWriter(output)));
        try
        {
            _templates[part].Render(state.Context);
        }
        finally
        {
            state.Context.PopOutput();
        }
    }

    private static State NewState()
    {
        var globals = new ScriptObject();
        var context = new TemplateContext { MemberRenamer = member => member.Name };
        context.PushGlobal(globals);
        return new State(context, globals);
    }

    private sealed record State(TemplateContext Context, ScriptObject Globals);

    /// <summary>Passes every write to the target; its text is never materialised.</summary>
    private sealed class ForwardingWriter(TextWriter target) : TextWriter
    {
        public override System.Text.Encoding Encoding => target.Encoding;

        public override void Write(char value) => target.Write(value);

        public override void Write(char[] buffer, int index, int count) => target.Write(buffer, index, count);

        public override void Write(string? value) => target.Write(value);

        public override void Write(ReadOnlySpan<char> buffer) => target.Write(buffer);

        public override string ToString() => string.Empty;
    }
}
