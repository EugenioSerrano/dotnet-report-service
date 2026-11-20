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

    /// <summary>The engine version the build resolved, read from the engine's own assembly.</summary>
    public static readonly string EngineVersion = EngineVersions.Of(typeof(Template));

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
    public string Version => EngineVersion;

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
            var template = ScribanParsing.Parse(path, source);
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
        PresentationSettings presentation,
        GuardedWriter writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(presentation);
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
        RemoveHostClock(templateContext);

        // One global scope for the part: the values of the render model and the nine
        // presentation functions, which are bound to this block's settings and to nothing else.
        var globals = new ScriptObject(context.Members().Count() + PresentationFunctionTable.FunctionCount);
        foreach (var member in context.Members())
        {
            globals[member.Key] = ToScriptValue(member.Value);
        }

        ScribanPresentationFunctions.Register(globals, presentation);
        templateContext.PushGlobal(globals);

        var rendered = TemplatePartNames.Of(part);
        try
        {
            var text = scribanTemplate.Render(templateContext);
            if (writer.BytesWritten == 0 && writer.CallCharacters == 0 && !string.IsNullOrEmpty(text))
            {
                writer.Write(text);
            }
        }
        catch (PresentationRefusedException)
        {
            throw;
        }
        catch (Scriban.Syntax.ScriptRuntimeException exception)
        {
            // A presentation refusal raised by one of the nine functions is the block's own
            // typed refusal, whatever the engine wrapped it in: it reaches the renderer as the
            // refusal it is, so the consumer reads DATA_INVALID or TEMPLATE_INVALID with its
            // reason, never the generic code of a step that could not finish.
            if (Find<PresentationRefusedException>(exception) is { } refusal)
            {
                throw refusal;
            }

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

    /// <summary>
    /// Removes the engine's own clock functions: a template's "now" is the job's acceptance
    /// instant, which it reads from the render model, so no function may answer the host's
    /// clock instead.
    /// </summary>
    private static void RemoveHostClock(TemplateContext context)
    {
        if (context.BuiltinObject is not ScriptObject builtins)
        {
            return;
        }

        Remove(builtins, "date", "now");
        Remove(builtins, "date", "now_utc");
        Remove(builtins, "time", "now");
    }

    private static void Remove(ScriptObject builtins, string member, string function)
    {
        if (builtins[member] is ScriptObject functions)
        {
            functions.Remove(function);
        }
    }

    // The first exception of <typeparamref name="TException"/> in the chain an engine wrapped a
    // refusal in, so a typed refusal is never lost to the engine's own exception type.
    private static TException? Find<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException found)
            {
                return found;
            }
        }

        return null;
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
        // One resolution for the render and the take's include check: a Scriban
        // include names a declared part's path, and nothing else is loaded.
        var resolved = ScribanIncludes.Resolve(templatePath);
        if (resolved is not null)
        {
            foreach (var part in Enum.GetValues<TemplatePart>())
            {
                if (string.Equals(template.Manifest.PathOf(part), resolved, StringComparison.Ordinal) && template.Has(part))
                {
                    return resolved;
                }
            }
        }

        throw new TemplatePackageException($"An include names '{templatePath}', which is not a part of the package.");
    }

    public string Load(TemplateContext context, SourceSpan callerSpan, string templatePath)
    {
        var resolved = ScribanIncludes.Resolve(templatePath);
        if (resolved is not null)
        {
            foreach (var part in Enum.GetValues<TemplatePart>())
            {
                if (string.Equals(template.Manifest.PathOf(part), resolved, StringComparison.Ordinal) && template.Has(part))
                {
                    return template.Source(part);
                }
            }
        }

        throw new TemplatePackageException($"An include names '{templatePath}', which is not a part of the package.");
    }

    public ValueTask<string?> LoadAsync(TemplateContext context, SourceSpan callerSpan, string templatePath) =>
        new(Load(context, callerSpan, templatePath));
}
