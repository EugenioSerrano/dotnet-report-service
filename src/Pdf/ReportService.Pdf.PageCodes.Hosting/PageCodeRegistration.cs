using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReportService.Pdf.PageCodes.QrCoder;
using ReportService.Pdf.Verification.PageCodes;

namespace ReportService.Pdf.PageCodes.Hosting;

/// <summary>The deployment's page-code generators cannot be registered as configured.</summary>
public sealed class PageCodeRegistrationException : Exception
{
    /// <summary>Creates the failure.</summary>
    /// <param name="message">What is wrong, naming the setting or the path.</param>
    /// <param name="inner">The underlying failure, when there is one.</param>
    public PageCodeRegistrationException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Registers the page-code generators a worker uses, from its configuration:
/// <list type="bullet">
/// <item><c>PageCodes:ReferenceGrid:Enabled</c> (default <c>false</c>) - the product's reference generator;</item>
/// <item><c>PageCodes:Examples:QrCoder:Enabled</c> (default <c>false</c>) - the opt-in QR example;</item>
/// <item><c>PageCodes:Assemblies</c> - assembly paths whose generators the deployment adds, each loaded
/// in its own load context with the dependencies beside it;</item>
/// <item><c>PageCodes:Disabled</c> - generator names removed last.</item>
/// </list>
/// Nothing is registered by default. The same settings must be given to the worker
/// that takes templates and to the worker that assembles documents.
/// </summary>
public static class PageCodeRegistration
{
    /// <summary>The configuration section.</summary>
    public const string Section = "PageCodes";

    /// <summary>Registers the deployment's generator registry.</summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The worker's configuration.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddPageCodeGenerators(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(_ => Build(configuration));
        return services;
    }

    /// <summary>Builds the registry the configuration describes.</summary>
    /// <param name="configuration">The worker's configuration.</param>
    /// <returns>The registry.</returns>
    /// <exception cref="PageCodeRegistrationException">A setting or an assembly cannot be used.</exception>
    public static PageCodeGeneratorRegistry Build(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(Section);
        var registered = new List<RegisteredPageCodeGenerator>();
        if (Enabled(section, "ReferenceGrid:Enabled"))
        {
            var reference = new ReferenceGridPageCodeGenerator();
            registered.Add(new RegisteredPageCodeGenerator(
                reference,
                PageCodeGeneratorDescriptor.Of(reference, PageCodeGeneratorOrigins.Reference)));
        }

        if (Enabled(section, "Examples:QrCoder:Enabled"))
        {
            var example = new QrCoderPageCodeGenerator();
            registered.Add(new RegisteredPageCodeGenerator(
                example,
                PageCodeGeneratorDescriptor.Of(example, PageCodeGeneratorOrigins.Example)));
        }

        foreach (var path in Values(section.GetSection("Assemblies")))
        {
            registered.AddRange(Load(path));
        }

        var disabled = Values(section.GetSection("Disabled")).ToHashSet(StringComparer.Ordinal);
        try
        {
            return new PageCodeGeneratorRegistry(registered.Where(item => !disabled.Contains(item.Generator.Name)));
        }
        catch (DuplicatePageCodeGeneratorException duplicate)
        {
            throw new PageCodeRegistrationException(duplicate.Message, duplicate);
        }
    }

    /// <summary>Loads the generators one configured assembly provides.</summary>
    /// <param name="path">The assembly's path.</param>
    /// <returns>Its generators.</returns>
    /// <exception cref="PageCodeRegistrationException">The assembly cannot be used.</exception>
    public static IReadOnlyList<RegisteredPageCodeGenerator> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new PageCodeRegistrationException($"the page-code assembly '{path}' does not exist");
        }

        var context = new PageCodeAssemblyLoadContext(full);
        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(full);
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException or IOException)
        {
            throw new PageCodeRegistrationException($"the page-code assembly '{path}' cannot be loaded", exception);
        }

        foreach (var reference in assembly.GetReferencedAssemblies())
        {
            try
            {
                context.LoadFromAssemblyName(reference);
            }
            catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                throw new PageCodeRegistrationException(
                    $"the page-code assembly '{path}' depends on '{reference.Name}', which cannot be found beside it",
                    exception);
            }
        }

        Type[] types;
        try
        {
            types = assembly.GetExportedTypes();
        }
        catch (Exception exception) when (exception is ReflectionTypeLoadException or FileNotFoundException or FileLoadException)
        {
            throw new PageCodeRegistrationException($"the page-code assembly '{path}' cannot be read", exception);
        }

        var generators = new List<RegisteredPageCodeGenerator>();
        foreach (var type in types.Where(type => type is { IsClass: true, IsAbstract: false }
            && typeof(IPageCodeGenerator).IsAssignableFrom(type)
            && type.GetConstructor(Type.EmptyTypes) is not null))
        {
            var generator = (IPageCodeGenerator)Activator.CreateInstance(type)!;
            generators.Add(new RegisteredPageCodeGenerator(
                generator,
                PageCodeGeneratorDescriptor.Of(generator, PageCodeGeneratorOrigins.Configured, path)));
        }

        if (generators.Count == 0)
        {
            throw new PageCodeRegistrationException($"the page-code assembly '{path}' contributes no page-code generator");
        }

        return generators;
    }

    private static bool Enabled(IConfigurationSection section, string key) =>
        bool.TryParse(section[key], out var enabled) && enabled;

    private static IEnumerable<string> Values(IConfigurationSection section)
    {
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            foreach (var item in section.Value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return item;
            }
        }

        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                yield return child.Value.Trim();
            }
        }
    }
}
