namespace StreamingDataSpike.Rendering;

/// <summary>
/// One template engine behind a common contract: the parts are prepared
/// (parsed or compiled) once, then rendered per part per record with a
/// context built for that call. A prepared renderer may be shared by
/// several threads as long as each render has its own context.
/// </summary>
internal interface IRecordRenderer
{
    /// <summary><c>scriban</c>, <c>handlebars</c> or <c>liquid</c>.</summary>
    string Engine { get; }

    /// <summary>Parses or compiles every part once.</summary>
    void Prepare(TemplateParts parts);

    /// <summary>Renders one part with the given context to <paramref name="output"/>.</summary>
    void Render(string part, RenderContext context, TextWriter output);
}

/// <summary>Creates the renderers by engine name.</summary>
internal static class Renderers
{
    /// <summary>The three engines, in report order.</summary>
    public static readonly string[] Engines = ["scriban", "handlebars", "liquid"];

    /// <summary>A new, unprepared renderer.</summary>
    public static IRecordRenderer Create(string engine) => engine switch
    {
        "scriban" => new ScribanRenderer(),
        "handlebars" => new HandlebarsRenderer(),
        "liquid" => new FluidRenderer(),
        _ => throw new ArgumentException("Unknown engine " + engine, nameof(engine)),
    };

    /// <summary>A renderer with the template's parts for that engine prepared.</summary>
    public static IRecordRenderer Prepared(string engine, string templateId, out double prepareMilliseconds, out TemplateParts parts)
    {
        parts = TemplateParts.Load(templateId, engine);
        var renderer = Create(engine);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        renderer.Prepare(parts);
        prepareMilliseconds = clock.Elapsed.TotalMilliseconds;
        return renderer;
    }
}
