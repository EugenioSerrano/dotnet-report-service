using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;

namespace ReportService.Templates;

/// <summary>
/// Composes a package with Scriban 7.5.1.
/// <para>
/// Parts are parsed once, at preparation, and never again; a fresh context is
/// built for every part, so nothing a template assigns survives the render call.
/// The engine's limits are the product's (10 000 loop iterations, 100 nested
/// calls, 1 MiB for one converted value, a one-second regular-expression
/// timeout), its cancellation token is the render's, and includes resolve only
/// inside the package. Escaping is template discipline: <c>html.escape</c> is
/// replaced by the product's one policy so that every engine encodes a data value
/// identically.
/// </para>
/// </summary>
public sealed class ScribanTemplateAdapter : ITemplateEngineAdapter
{
    /// <summary>The engine name the adapter composes, as a package manifest names it.</summary>
    public const string EngineName = "scriban";

    /// <summary>The package's declared engine version this adapter composes.</summary>
    public const string PinnedVersion = "7.5.1";

    /// <summary>The most loop iterations one render may take.</summary>
    public const int LoopLimit = 10_000;

    /// <summary>The most nested calls one render may take.</summary>
    public const int RecursiveLimit = 100;

    /// <summary>The most characters one converted value may produce.</summary>
    public const int LimitToString = 1024 * 1024;

    /// <summary>The deadline of one regular-expression evaluation.</summary>
    public static readonly TimeSpan RegexTimeOut = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public string Engine => EngineName;

    /// <inheritdoc />
    public PreparedTemplate Prepare(RenderManifest manifest, ITemplatePackageFiles files)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(files);
        if (!string.Equals(manifest.Engine, EngineName, StringComparison.OrdinalIgnoreCase))
        {
            throw new TemplatePackageException($"The package declares the engine '{manifest.Engine}', which this adapter does not compose.");
        }

        var parsed = new Dictionary<TemplatePart, PreparedPart>();
        foreach (var part in Enum.GetValues<TemplatePart>())
        {
            var path = manifest.PathOf(part);
            if (path is null)
            {
                continue;
            }

            var source = files.ReadAllText(path);
            var template = Template.Parse(source, path, parserOptions: null, lexerOptions: null);
            if (template.HasErrors)
            {
                throw new TemplatePackageException($"The {TemplatePartNames.Of(part)} part of the package cannot be parsed: {string.Join("; ", template.Messages)}");
            }

            parsed[part] = new PreparedPart(template, source);
        }

        return new PreparedTemplate(manifest, parsed);
    }

    /// <inheritdoc />
    public void RenderPart(
        PreparedTemplate template,
        TemplatePart part,
        RenderContext context,
        GuardedWriter writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);

        var scribanTemplate = (Template)template.Part(part);
        writer.BeginCall();

        var templateContext = new TemplateContext
        {
            LoopLimit = LoopLimit,
            RecursiveLimit = RecursiveLimit,
            LimitToString = LimitToString,
            RegexTimeOut = RegexTimeOut,
            CancellationToken = cancellationToken,
            NewLine = "\n",
            StrictVariables = false,
            EnableRelaxedMemberAccess = true,
            TemplateLoader = new PackageTemplateLoader(template),
        };
        templateContext.PushOutput(new TextWriterOutput(writer));
        ApplyEscapingPolicy(templateContext);
        foreach (var member in context.Members())
        {
            var globals = new ScriptObject(1);
            globals[member.Key] = ToScriptValue(member.Value);
            templateContext.PushGlobal(globals);
        }

        var rendered = TemplatePartNames.Of(part);
        try
        {
            var text = scribanTemplate.Render(templateContext);
            if (writer.BytesWritten == 0 && writer.CallCharacters == 0 && !string.IsNullOrEmpty(text))
            {
                writer.Write(text);
            }
        }
        catch (Scriban.Syntax.ScriptRuntimeException exception)
        {
            throw new TemplatePackageException($"The {rendered} part failed to render: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            throw new TemplatePackageException($"The {rendered} part failed to render: {exception.Message}");
        }
    }

    /// <summary>
    /// Replaces <c>html.escape</c> with the product's one escaping policy, so a
    /// template written as the authoring guide requires encodes data exactly as
    /// every other engine does.
    /// </summary>
    private static void ApplyEscapingPolicy(TemplateContext context)
    {
        if (context.BuiltinObject is not ScriptObject builtins)
        {
            return;
        }

        var html = builtins["html"] as ScriptObject;
        if (html is null)
        {
            html = new ScriptObject();
            builtins["html"] = html;
        }

        html["escape"] = DynamicCustomFunction.Create(new Func<string?, object?>(value => HtmlEncodingPolicy.Encode(value)));
    }

    /// <summary>Converts a plain value of the render model into the engine's own plain value.</summary>
    private static object? ToScriptValue(object? value) => value switch
    {
        null => null,
        string or bool => value,
        IReadOnlyDictionary<string, object?> members => ToScriptObject(members),
        IEnumerable<object?> items => ToScriptArray(items),
        _ => value,
    };

    private static ScriptObject ToScriptObject(IReadOnlyDictionary<string, object?> members)
    {
        var scriptObject = new ScriptObject(members.Count);
        foreach (var (name, member) in members)
        {
            scriptObject[name] = ToScriptValue(member);
        }

        return scriptObject;
    }

    private static ScriptArray ToScriptArray(IEnumerable<object?> items)
    {
        var array = new ScriptArray();
        foreach (var item in items)
        {
            array.Add(ToScriptValue(item));
        }

        return array;
    }
}

/// <summary>
/// Reads the parts a package includes from the package itself: a name is resolved
/// inside the package and nowhere else, so no include reaches the host.
/// </summary>
internal sealed class PackageTemplateLoader(PreparedTemplate template) : ITemplateLoader
{
    public string GetPath(TemplateContext context, SourceSpan callerSpan, string templatePath)
    {
        // An include of another declared part is served from what preparation already read.
        foreach (var part in Enum.GetValues<TemplatePart>())
        {
            if (string.Equals(template.Manifest.PathOf(part), templatePath, StringComparison.Ordinal) && template.Has(part))
            {
                return templatePath;
            }
        }

        throw new TemplatePackageException($"An include names '{templatePath}', which is not a part of the package.");
    }

    public string Load(TemplateContext context, SourceSpan callerSpan, string templatePath)
    {
        foreach (var part in Enum.GetValues<TemplatePart>())
        {
            if (string.Equals(template.Manifest.PathOf(part), templatePath, StringComparison.Ordinal) && template.Has(part))
            {
                return template.Source(part);
            }
        }

        throw new TemplatePackageException($"An include names '{templatePath}', which is not a part of the package.");
    }

    public ValueTask<string?> LoadAsync(TemplateContext context, SourceSpan callerSpan, string templatePath) =>
        new(Load(context, callerSpan, templatePath));
}
