using System.Reflection;

namespace ReportService.Pdf.Verification.PageCodes;

/// <summary>Where a registered generator came from.</summary>
public static class PageCodeGeneratorOrigins
{
    /// <summary>The product's library-free reference generator.</summary>
    public const string Reference = "reference";

    /// <summary>An example generator the product ships, enabled by configuration.</summary>
    public const string Example = "example";

    /// <summary>A generator loaded from an assembly the deployment's configuration names.</summary>
    public const string Configured = "configured";
}

/// <summary>What the metrics record about the generator that drew a document's codes.</summary>
/// <param name="Name">The generator's name.</param>
/// <param name="AssemblyName">The simple name of the assembly that holds it.</param>
/// <param name="AssemblyVersion">That assembly's informational version, else its version.</param>
/// <param name="Origin">One of <see cref="PageCodeGeneratorOrigins"/>.</param>
/// <param name="Path">The configured path the assembly was loaded from, for a configured generator.</param>
public sealed record PageCodeGeneratorDescriptor(
    string Name,
    string AssemblyName,
    string AssemblyVersion,
    string Origin,
    string? Path)
{
    /// <summary>Describes a generator instance.</summary>
    /// <param name="generator">The generator.</param>
    /// <param name="origin">Where it came from.</param>
    /// <param name="path">The configured path, for a configured generator.</param>
    /// <returns>The descriptor.</returns>
    public static PageCodeGeneratorDescriptor Of(IPageCodeGenerator generator, string origin, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var assembly = generator.GetType().Assembly;
        var name = assembly.GetName();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? name.Version?.ToString()
            ?? "unknown";
        return new PageCodeGeneratorDescriptor(generator.Name, name.Name ?? "unknown", version, origin, path);
    }
}

/// <summary>One generator the deployment registered, with its descriptor.</summary>
/// <param name="Generator">The generator.</param>
/// <param name="Descriptor">What the metrics record about it.</param>
public sealed record RegisteredPageCodeGenerator(IPageCodeGenerator Generator, PageCodeGeneratorDescriptor Descriptor);

/// <summary>Two generators were registered under one name.</summary>
public sealed class DuplicatePageCodeGeneratorException : Exception
{
    /// <summary>Creates the failure.</summary>
    /// <param name="name">The name registered twice.</param>
    public DuplicatePageCodeGeneratorException(string name)
        : base($"two page-code generators are registered under the name '{name}'") => GeneratorName = name;

    /// <summary>The name registered twice.</summary>
    public string GeneratorName { get; }
}

/// <summary>
/// The generators a deployment registered, by name. Exactly one generator per name;
/// a name with none is a name the deployment does not support.
/// </summary>
public sealed class PageCodeGeneratorRegistry
{
    private readonly Dictionary<string, RegisteredPageCodeGenerator> _byName = new(StringComparer.Ordinal);

    /// <summary>Builds the registry.</summary>
    /// <param name="generators">The registered generators.</param>
    /// <exception cref="DuplicatePageCodeGeneratorException">Two generators share a name.</exception>
    public PageCodeGeneratorRegistry(IEnumerable<RegisteredPageCodeGenerator> generators)
    {
        ArgumentNullException.ThrowIfNull(generators);
        foreach (var registered in generators)
        {
            if (!_byName.TryAdd(registered.Generator.Name, registered))
            {
                throw new DuplicatePageCodeGeneratorException(registered.Generator.Name);
            }
        }
    }

    /// <summary>The names the deployment supports, in ordinal order.</summary>
    public IReadOnlyList<string> Names => [.. _byName.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Finds the generator registered under a name.</summary>
    /// <param name="name">The declared name.</param>
    /// <param name="generator">The generator, when one is registered.</param>
    /// <returns>Whether one is registered.</returns>
    public bool TryResolve(string name, out RegisteredPageCodeGenerator generator) =>
        _byName.TryGetValue(name, out generator!);
}
