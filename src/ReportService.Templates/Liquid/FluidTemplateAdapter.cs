using System.Text;
using System.Text.Encodings.Web;
using Fluid;
using Fluid.Values;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace ReportService.Templates;

/// <summary>
/// Composes a package with Fluid.Core 2.40.0 (Liquid). Every part is parsed once, at
/// preparation, with the one parser <see cref="FluidParsing"/> creates; a render builds its
/// own options - the engine's limits, the block's culture and the presentation filters - and
/// its own context, so nothing a template assigns survives a part. Includes resolve to the
/// package's declared parts and nothing else, and each render call writes through the
/// product's one escaping policy.
/// </summary>
public sealed class FluidTemplateAdapter : ITemplateEngineAdapter
{
    /// <summary>The engine name the adapter composes, as a package manifest names it.</summary>
    public const string EngineName = "liquid";

    /// <summary>The engine version the build resolved, read from the engine's own assembly.</summary>
    public static readonly string EngineVersion = EngineVersions.Of(typeof(FluidParser));

    /// <summary>The most steps one render may take.</summary>
    public const int MaxSteps = 100_000;

    /// <summary>The most nested renders one render may take.</summary>
    public const int MaxRecursion = 100;

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

        var parser = FluidParsing.Create();
        var parsed = new Dictionary<TemplatePart, PreparedPart>();
        foreach (var part in Enum.GetValues<TemplatePart>())
        {
            var path = manifest.PathOf(part);
            if (path is null)
            {
                continue;
            }

            var source = files.ReadAllText(path);
            if (!parser.TryParse(source, out var template, out _))
            {
                throw new TemplatePackageException($"The {TemplatePartNames.Of(part)} part of the package cannot be parsed at '{path}'.");
            }

            parsed[part] = new PreparedPart(template!, source);
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
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(writer);

        var fluid = (IFluidTemplate)template.Part(part);
        var rendered = TemplatePartNames.Of(part);
        var options = new TemplateOptions
        {
            MaxSteps = MaxSteps,
            MaxRecursion = MaxRecursion,
            CultureInfo = settings.Culture,
            StrictVariables = false,
            FileProvider = new PackageFileProvider(template),
        };
        FluidPresentationFilters.Register(options, settings);

        var model = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in context.Members())
        {
            model[name] = value;
        }

        var templateContext = new TemplateContext(model, options, allowModelMembers: true);
        writer.BeginCall();
        try
        {
            fluid.RenderAsync(writer, FluidPolicyTextEncoder.Instance, templateContext).AsTask().GetAwaiter().GetResult();
        }
        catch (PresentationRefusedException)
        {
            throw;
        }
        catch (Exception exception) when (Find<PresentationRefusedException>(exception) is { } refusal)
        {
            // The engine may wrap what a filter raised: the block's own typed refusal is
            // rethrown as itself, so the consumer reads DATA_INVALID or TEMPLATE_INVALID with
            // its reason and never the generic code of a step that could not finish.
            throw refusal;
        }
        catch (FluidException exception) when (LimitOf(exception) is { } limit)
        {
            throw TemplateGuardException.Quota(limit, $"The {rendered} part passed the engine's {limit} limit.");
        }
        catch (FluidException)
        {
            throw new TemplatePackageException($"The {rendered} part failed to render.");
        }
        catch (FileNotFoundException)
        {
            // An include names a file the package does not declare, which is the package's
            // confinement rule: nothing outside its parts is loaded.
            throw new TemplatePackageException($"The {rendered} part names an include the package does not declare.");
        }
        catch (InvalidOperationException)
        {
            throw new TemplatePackageException($"The {rendered} part failed to render.");
        }
    }

    // Fluid reports a passed limit as its own exception, without a member that names which
    // one: the message is the engine's vocabulary and carries no value of the data.
    private static string? LimitOf(FluidException exception)
    {
        var message = exception.Message;
        if (message.Contains("step", StringComparison.OrdinalIgnoreCase))
        {
            return "engine-steps";
        }

        return message.Contains("recursion", StringComparison.OrdinalIgnoreCase) ? "engine-recursion" : null;
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
}

/// <summary>
/// The product's one escaping policy as Fluid's encoder: every value a template writes with
/// <c>{{ }}</c> is written exactly as <see cref="HtmlEncodingPolicy.Encode"/> encodes it.
/// </summary>
internal sealed class FluidPolicyTextEncoder : TextEncoder
{
    /// <summary>The one encoder of the adapter.</summary>
    public static FluidPolicyTextEncoder Instance { get; } = new();

    /// <inheritdoc />
    public override int MaxOutputCharactersPerInputCharacter => 8;

    /// <inheritdoc />
    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength) => textLength == 0 ? -1 : 0;

    /// <inheritdoc />
    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        var encoded = HtmlEncodingPolicy.Encode(char.ConvertFromUtf32(unicodeScalar));
        if (encoded.Length > bufferLength)
        {
            numberOfCharactersWritten = 0;
            return false;
        }

        for (var index = 0; index < encoded.Length; index++)
        {
            buffer[index] = encoded[index];
        }

        numberOfCharactersWritten = encoded.Length;
        return true;
    }

    /// <inheritdoc />
    public override bool WillEncode(int unicodeScalar) => true;
}

/// <summary>
/// The package's own files as Fluid's include provider: an include names a declared part of
/// the package, and nothing else is loaded - no file system, no host path.
/// </summary>
internal sealed class PackageFileProvider(PreparedTemplate template) : IFileProvider
{
    /// <inheritdoc />
    public IFileInfo GetFileInfo(string subpath)
    {
        foreach (var part in Enum.GetValues<TemplatePart>())
        {
            if (!template.Has(part))
            {
                continue;
            }

            var path = template.Manifest.PathOf(part);
            if (string.Equals(subpath, TemplatePartNames.Of(part), StringComparison.Ordinal)
                || string.Equals(subpath, path, StringComparison.Ordinal))
            {
                return new PackageFileInfo(subpath, template.Source(part));
            }
        }

        return new PackageFileInfo(subpath, source: null);
    }

    /// <inheritdoc />
    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

    /// <inheritdoc />
    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
}

/// <summary>One file of the package, held in memory as the package's own text.</summary>
internal sealed class PackageFileInfo(string name, string? source) : IFileInfo
{
    /// <inheritdoc />
    public bool Exists => source is not null;

    /// <inheritdoc />
    public long Length => source is null ? 0 : Encoding.UTF8.GetByteCount(source);

    /// <inheritdoc />
    public string? PhysicalPath => null;

    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;

    /// <inheritdoc />
    public bool IsDirectory => false;

    /// <inheritdoc />
    public Stream CreateReadStream() =>
        source is null
            ? throw new FileNotFoundException($"The package declares no '{name}' file.")
            : new MemoryStream(Encoding.UTF8.GetBytes(source), writable: false);
}
