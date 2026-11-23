using System.Globalization;

namespace ReportService.Eng.TestRunner;

/// <summary>
/// Resolves the directory the spawned test processes write their temporary files to
/// (their <c>TMP</c>/<c>TEMP</c>, hence every test's <see cref="Path.GetTempPath"/> and
/// <see cref="Directory.CreateTempSubdirectory(string)"/>). Routing test temp to a fast,
/// AV-excluded location keeps the machine's real-time antivirus from synchronously
/// scanning freshly-written test files on open — the ~240 s stall and flakiness
/// diagnosed in BUG-052 (the block is in the Win32 <c>CreateFile</c>, not in product
/// code; the product read path is correct).
/// <para>
/// The resolution is: the <see cref="EnvVarName"/> environment variable when set and
/// write-probe-usable; else the <see cref="DefaultRoot"/> (the team's Dev-Drive default)
/// when write-probe-usable; else the normal system temp (<see cref="Path.GetTempPath"/>).
/// It <b>never throws and never fails the suite</b>: an unset variable or a
/// missing/unwritable path falls through to the system temp, i.e. today's behaviour.
/// </para>
/// </summary>
public static class TestTempRoot
{
    /// <summary>The overridable setting that selects the test-temp root.</summary>
    public const string EnvVarName = "REPORTSERVICE_TEST_TEMP";

    /// <summary>The default root when the variable is unset: the team's Dev-Drive location.</summary>
    public const string DefaultRoot = @"D:\Temp";

    /// <summary>
    /// The longest root accepted. Kept well under the product
    /// <c>FilesystemObjectStore</c> <c>MaxPathLength = 248</c>, leaving ample budget for a
    /// test's <c>CreateTempSubdirectory</c> name and the fixture paths nested under it. The
    /// default <c>D:\Temp</c> (7 chars) is comfortably within it.
    /// </summary>
    public const int MaxRootLength = 128;

    /// <summary>Which rule chose the resolved root.</summary>
    public enum Source
    {
        /// <summary>The environment variable named a usable root.</summary>
        Configured,

        /// <summary>The variable was unusable/unset; the Dev-Drive default was usable.</summary>
        Default,

        /// <summary>Neither was usable; the normal system temp is used (today's behaviour).</summary>
        SystemFallback,
    }

    /// <summary>The chosen root and the rule that chose it.</summary>
    /// <param name="Root">The directory to use as the test-temp root.</param>
    /// <param name="Source">Which rule chose it.</param>
    public readonly record struct Resolution(string Root, Source Source);

    /// <summary>
    /// Resolves the root deterministically from an injected environment lookup and default,
    /// with an injectable usability probe (so a test is deterministic even on a machine that
    /// has <c>D:\Temp</c>). Never throws.
    /// </summary>
    /// <param name="getEnvironmentVariable">Reads an environment variable by name (inject <see cref="Environment.GetEnvironmentVariable(string)"/> in production).</param>
    /// <param name="defaultRoot">The default root to try when the variable is unusable.</param>
    /// <param name="isUsable">Decides whether a root is usable (defaults to <see cref="IsUsable"/>, a real write-probe).</param>
    public static Resolution Resolve(
        Func<string, string?> getEnvironmentVariable,
        string defaultRoot,
        Func<string, bool>? isUsable = null)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        isUsable ??= IsUsable;

        string? configured;
        try
        {
            configured = getEnvironmentVariable(EnvVarName);
        }
        catch
        {
            configured = null;
        }

        if (IsCandidate(configured) && Usable(isUsable, configured!))
        {
            return new Resolution(configured!, Source.Configured);
        }

        if (IsCandidate(defaultRoot) && Usable(isUsable, defaultRoot))
        {
            return new Resolution(defaultRoot, Source.Default);
        }

        return new Resolution(SystemTemp(), Source.SystemFallback);
    }

    /// <summary>A fresh environment dictionary carrying <c>TMP</c> and <c>TEMP</c> for a spawned test process.</summary>
    public static Dictionary<string, string> SpawnEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        ApplyTo(environment);
        return environment;
    }

    /// <summary>
    /// The designated CLI input that keeps a <c>dotnet run</c> on a C# AppHost from being
    /// rerouted through the CLI run hook. The run hook, when it fires, relaunches the host
    /// with <c>--no-build</c> and the default (Debug) build, silently dropping an explicit
    /// <c>-c Release</c> and <c>--no-launch-profile</c> on the spawned command; the Release
    /// host exe is then never started. Setting this to <c>true</c> for the spawn that
    /// launches the AppHost keeps the explicit configuration and launch-profile flags, so
    /// the Release host runs as asked. It is inert for a spawn that is not an AppHost run.
    /// </summary>
    public const string SuppressCliRunHookName = "ASPIRE_SUPPRESS_CLI_RUN_HOOK";

    /// <summary>
    /// The spawn environment for the AppHost <c>dotnet run</c> launch: the usual
    /// <c>TMP</c>/<c>TEMP</c> routing plus <see cref="SuppressCliRunHookName"/> set so the
    /// CLI run hook does not reroute the run and drop the explicit configuration flags.
    /// </summary>
    public static Dictionary<string, string> AppHostRunSpawnEnvironment()
    {
        var environment = SpawnEnvironment();
        environment[SuppressCliRunHookName] = "true";
        return environment;
    }

    /// <summary>
    /// Merges <c>TMP</c> and <c>TEMP</c> (the once-resolved root) into an existing environment
    /// dictionary, keeping its other entries. Used at the spawn site whose dictionary already
    /// carries entries.
    /// </summary>
    public static void ApplyTo(IDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var root = Current.Value.Root;
        environment["TMP"] = root;
        environment["TEMP"] = root;
    }

    /// <summary>
    /// Creates the root if needed and proves a write by creating and deleting a probe file;
    /// never throws, and always deletes the probe (including on failure). Touches only a
    /// uniquely-named probe file at the root, never any sample or fixture path.
    /// </summary>
    public static bool IsUsable(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string? probe = null;
        try
        {
            Directory.CreateDirectory(root);
            probe = Path.Combine(root, "reportservice-temp-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "probe");
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (probe is not null && File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch
            {
                // Best effort: a probe that cannot be deleted never fails resolution.
            }
        }
    }

    private static bool IsCandidate(string? root) =>
        !string.IsNullOrWhiteSpace(root) && root!.Length <= MaxRootLength;

    private static bool Usable(Func<string, bool> isUsable, string root)
    {
        try
        {
            return isUsable(root);
        }
        catch
        {
            return false;
        }
    }

    private static string SystemTemp()
    {
        try
        {
            return Path.GetTempPath();
        }
        catch
        {
            return ".";
        }
    }

    // Resolved once per runner invocation, from the real environment and default, and the
    // chosen root logged once for diagnosis.
    private static readonly Lazy<Resolution> Current = new(() =>
    {
        var resolution = Resolve(Environment.GetEnvironmentVariable, DefaultRoot);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"test temp root: {resolution.Root} (source: {resolution.Source})"));
        return resolution;
    });
}
