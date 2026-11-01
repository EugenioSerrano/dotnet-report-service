using System.Reflection;

namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>
/// The product version a process reports. The informational version carries the
/// pre-release label and the commit the build came from
/// (<c>0.2.0-dev+&lt;commit&gt;</c>, release §3.1); a build of a tree without git
/// carries <c>+unknown</c>. Reading and parsing never throw: an absent or
/// malformed value reports <see cref="UnknownCommit"/>.
/// </summary>
public static class ProductVersion
{
    /// <summary>What is reported when the informational version carries no commit.</summary>
    public const string UnknownCommit = "unknown";

    /// <summary>The informational version of the running entry assembly.</summary>
    public static string Informational => InformationalOf(Assembly.GetEntryAssembly() ?? typeof(ProductVersion).Assembly);

    /// <summary>The informational version of <paramref name="assembly"/>, or its version, or <see cref="UnknownCommit"/>.</summary>
    public static string InformationalOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString() ?? UnknownCommit;
    }

    /// <summary>
    /// The commit suffix of an informational version — everything after the
    /// metadata separator. A value without one, an empty suffix or a value that
    /// is not usable reports <see cref="UnknownCommit"/> and never throws.
    /// </summary>
    public static string CommitOf(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return UnknownCommit;
        }

        var separator = informationalVersion.IndexOf('+');
        if (separator < 0 || separator == informationalVersion.Length - 1)
        {
            return UnknownCommit;
        }

        var commit = informationalVersion[(separator + 1)..].Trim();
        return commit.Length == 0 ? UnknownCommit : commit;
    }
}
