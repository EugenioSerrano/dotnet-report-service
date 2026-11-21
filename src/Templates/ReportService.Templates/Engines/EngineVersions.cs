using System.Reflection;

namespace ReportService.Templates;

/// <summary>
/// The version an engine actually runs, read from the assembly that implements it. A
/// version copied into a constant would drift from the package the build resolved, so the
/// adapters report what the loaded assembly says.
/// </summary>
internal static class EngineVersions
{
    /// <summary>The major, minor and build of the assembly that declares <paramref name="engineType"/>.</summary>
    /// <param name="engineType">A type of the engine's own assembly.</param>
    public static string Of(Type engineType)
    {
        ArgumentNullException.ThrowIfNull(engineType);
        var assembly = engineType.Assembly;

        // The package's own version is its informational version; the assembly version of
        // these packages is pinned to the major only (Scriban reports 7.0.0 for 7.5.1), so
        // reading it would name an engine the build did not resolve.
        if (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } informational)
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        var version = assembly.GetName().Version
            ?? throw new InvalidOperationException($"The assembly of {engineType.FullName} reports no version.");
        return version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>The major version of an engine's version text, or <see langword="null"/> when it has none.</summary>
    /// <param name="version">A version text such as <c>7.5.1</c>.</param>
    public static int? MajorOf(string? version) =>
        Version.TryParse(version, out var parsed) ? parsed.Major : null;

    /// <summary>The assembly of a type, kept out of the public surface of the adapters.</summary>
    /// <param name="type">The type.</param>
    public static Assembly AssemblyOf(Type type) => type.Assembly;
}
