using System.Text;
using HandlebarsDotNet;

namespace ReportService.Templates;

/// <summary>
/// Composes a package with Handlebars.Net 2.4.3. A package's parts are parsed and compiled
/// once, at preparation, in one environment created by <see cref="HandlebarsParsing"/> and
/// then given this product's escaping policy and its presentation helpers; nothing a template
/// assigns survives a part, because a fresh context is built for every part. The package's
/// declared parts are its partials, so an include names a part of the package and nothing
/// else is loaded; no file system is configured.
/// </summary>
public sealed class HandlebarsTemplateAdapter : ITemplateEngineAdapter
{
    /// <summary>The engine name the adapter composes, as a package manifest names it.</summary>
    public const string EngineName = "handlebars";

    /// <summary>The engine version the build resolved, read from the engine's own assembly.</summary>
    public static readonly string EngineVersion = EngineVersions.Of(typeof(IHandlebars));

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

        var handlebars = HandlebarsParsing.Create();
        handlebars.Configuration.TextEncoder = HandlebarsPolicyTextEncoder.Instance;
        HandlebarsPresentationHelpers.Register(handlebars);

        var parsed = new Dictionary<TemplatePart, PreparedPart>();
        foreach (var part in Enum.GetValues<TemplatePart>())
        {
            var path = manifest.PathOf(part);
            if (path is null)
            {
                continue;
            }

            var source = files.ReadAllText(path);

            // The declared parts are the package's partials: an include resolves to one of
            // them by its manifest name, and a name the package does not declare is refused
            // when the part is compiled or rendered.
            try
            {
                handlebars.RegisterTemplate(TemplatePartNames.Of(part), source);
                parsed[part] = new PreparedPart(handlebars.Compile(new StringReader(source)), source);
            }
            catch (HandlebarsException)
            {
                throw new TemplatePackageException($"The {TemplatePartNames.Of(part)} part of the package cannot be parsed at '{path}'.");
            }
        }

        return new PreparedTemplate(manifest, parsed);
    }

    /// <inheritdoc />
    public void RenderPart(
        PreparedTemplate template,
        TemplatePart part,
        RenderContext context,
        PresentationSettings settings,
        GuardedWriter writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);

        var compiled = (HandlebarsTemplate<TextWriter, object, object>)template.Part(part);
        var rendered = TemplatePartNames.Of(part);
        writer.BeginCall();

        var model = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in context.Members())
        {
            model[name] = value;
        }

        try
        {
            compiled(writer, model, model);
        }
        catch (Exception exception) when (Find<PresentationRefusedException>(exception) is { } refusal)
        {
            throw refusal;
        }
        catch (HandlebarsException)
        {
            throw new TemplatePackageException($"The {rendered} part failed to render.");
        }
        catch (HandlebarsDotNet.Compiler.HandlebarsUndefinedBindingException)
        {
            throw new TemplatePackageException($"The {rendered} part failed to render.");
        }
    }

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
}

/// <summary>
/// The product's one escaping policy as Handlebars' encoder: every value a template writes
/// with <c>{{ }}</c> is written exactly as <see cref="WebUtility.HtmlEncode"/> encodes it.
/// </summary>
internal sealed class HandlebarsPolicyTextEncoder : ITextEncoder
{
    /// <summary>The one encoder of the adapter.</summary>
    public static HandlebarsPolicyTextEncoder Instance { get; } = new();

    /// <inheritdoc />
    public void Encode(StringBuilder? stringBuilder, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (stringBuilder is not null)
        {
            writer.Write(HtmlEncodingPolicy.Encode(stringBuilder.ToString()));
        }
    }

    /// <inheritdoc />
    public void Encode(string? value, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(HtmlEncodingPolicy.Encode(value));
    }

    /// <inheritdoc />
#pragma warning disable CS8769 // The engine's own member is nullable-oblivious.
    void ITextEncoder.Encode<T>(T value, TextWriter writer)
#pragma warning restore CS8769
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null)
        {
            return;
        }

        writer.Write(HtmlEncodingPolicy.Encode(
            value as string ?? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));
    }
}
