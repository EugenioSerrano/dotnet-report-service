using System.Globalization;
using Fluid;

namespace StreamingDataSpike.Rendering;

/// <summary>
/// Liquid (Fluid): one parser, each part parsed once, one shared
/// <see cref="TemplateOptions"/> (invariant culture, step limit); every render
/// gets a new <see cref="TemplateContext"/> and goes through the policy text
/// encoder, so every <c>{{ }}</c> output is escaped.
/// </summary>
internal sealed class FluidRenderer : IRecordRenderer
{
    /// <summary>Step limit per part render used by the measurements.</summary>
    public const int MaxSteps = 100_000;

    private static readonly FluidParser Parser = new();
    private readonly Dictionary<string, IFluidTemplate> _templates = new(StringComparer.Ordinal);
    private readonly TemplateOptions _options = new() { CultureInfo = CultureInfo.InvariantCulture, MaxSteps = MaxSteps };

    public string Engine => "liquid";

    public void Prepare(TemplateParts parts)
    {
        foreach (var (name, (text, path)) in parts.Parts)
        {
            if (!Parser.TryParse(text, out var template, out var error))
            {
                throw new InvalidOperationException($"Liquid part {path} does not parse: {error}");
            }

            _templates[name] = template;
        }
    }

    public void Render(string part, RenderContext context, TextWriter output)
    {
        var templateContext = context.ReuseEngineState && context.EngineState is TemplateContext reused ? reused : new TemplateContext(_options);
        if (context.ReuseEngineState)
        {
            context.EngineState = templateContext;
        }

        templateContext.SetValue("header", context.Header);
        templateContext.SetValue("item", context.Item);
        templateContext.SetValue("group", context.Group);
        templateContext.SetValue("block", context.Block);
        templateContext.SetValue("aggregates", context.Aggregates);
        templateContext.SetValue("render", context.Render);

        // The engine is asynchronous; the harness is synchronous, so the
        // render is awaited here, at the edge, and only when it did not
        // complete synchronously.
        var pending = _templates[part].RenderAsync(output, HtmlPolicyTextEncoder.Instance, templateContext);
        if (!pending.IsCompletedSuccessfully)
        {
            pending.AsTask().GetAwaiter().GetResult();
        }
    }
}
