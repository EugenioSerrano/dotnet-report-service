using System.Diagnostics;
using System.Globalization;

namespace ReportService.Workers.Pdf.Rendering;

/// <summary>
/// The globalisation data of the process that prints: the ICU major version and the
/// time-zone data version, read once from the artefacts the runtime itself loads and kept
/// for the process's life. They are recorded with every committed block so a printed
/// document can be explained years later, when neither the image nor the data it shipped
/// still exists.
/// <para>
/// The read is a bounded probe over a short list of known paths per platform: the ICU
/// libraries of a Linux image name their major in the file name, and a Windows host carries
/// the version in the file's own metadata; the zone data names its version in the first line
/// of the database file it ships, and a Windows host in the file's metadata. The artefact is
/// never guessed and "unknown" is never recorded: a probe that finds nothing is an error of
/// the process, because a value that names nothing reproduces nothing.
/// </para>
/// </summary>
public sealed record GlobalisationProvenance(string IcuVersion, string TzDataVersion)
{
    private static readonly Lazy<GlobalisationProvenance> Cached =
        new(OfThisProcess, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The provenance of this process, read once.</summary>
    public static GlobalisationProvenance Current => Cached.Value;

    /// <summary>Reads the provenance of this process from the artefacts it runs on.</summary>
    /// <exception cref="InvalidOperationException">One of the two versions could not be read, or the process runs with globalisation disabled.</exception>
    public static GlobalisationProvenance OfThisProcess() =>
        new(ReadIcu() ?? throw Missing("ICU"), ReadTimeZoneData() ?? throw Missing("time-zone data"));

    /// <summary>
    /// Reads the ICU major version from the first artefact that names it, or answers
    /// <see langword="null"/> when none does.
    /// </summary>
    /// <param name="root">The directory the Linux libraries are looked under, for a test; the platform's own when null.</param>
    /// <param name="windowsDirectory">The Windows directory, for a test; the platform's own when null.</param>
    /// <exception cref="InvalidOperationException">The process runs with globalisation disabled, so it has no ICU data to name.</exception>
    public static string? ReadIcu(string? root = null, string? windowsDirectory = null)
    {
        // A process built or run without globalisation has no ICU to name: the shipped images
        // never set it, and "invariant" names no artefact, so the process refuses to record a
        // version that would reproduce nothing (D10, P5). It is an error of the process, never
        // a value of the record.
        if (AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant) && invariant)
        {
            throw new InvalidOperationException(
                "The process runs with globalisation disabled (System.Globalization.Invariant) and cannot name the ICU version it prints with; a block printed without ICU data is never recorded.");
        }

        // An explicit root is probed whatever the platform: a case reads a folder that looks
        // like an image's without being on one.
        if (root is not null || !OperatingSystem.IsWindows())
        {
            var libraryRoot = root ?? "/usr/lib";
            foreach (var directory in new[]
                     {
                         Path.Combine(libraryRoot, "x86_64-linux-gnu"),
                         Path.Combine(libraryRoot, "aarch64-linux-gnu"),
                         libraryRoot,
                     })
            {
                if (MajorOfIcuLibrary(directory) is { } major)
                {
                    return major;
                }
            }

            if (root is not null)
            {
                return null;
            }

            if (MajorOfIcuLibrary("/usr/local/lib") is { } local)
            {
                return local;
            }
        }

        return OperatingSystem.IsWindows()
            ? VersionOfWindowsFile(Path.Combine(windowsDirectory ?? Environment.SystemDirectory, "icu.dll"))
            : null;
    }

    /// <summary>
    /// Reads the time-zone data version from the first artefact that names it, or answers
    /// <see langword="null"/> when none does.
    /// </summary>
    /// <param name="zoneInfoRoot">The zone-info directory, for a test; the platform's own when null.</param>
    /// <param name="windowsDirectory">The Windows directory, for a test; the platform's own when null.</param>
    public static string? ReadTimeZoneData(string? zoneInfoRoot = null, string? windowsDirectory = null)
    {
        var zoneInfo = zoneInfoRoot ?? "/usr/share/zoneinfo";
        if (zoneInfoRoot is not null || !OperatingSystem.IsWindows())
        {
            foreach (var file in new[] { Path.Combine(zoneInfo, "tzdata.zi"), Path.Combine(zoneInfo, "+VERSION") })
            {
                if (FirstLine(file) is { } line && line.Trim() is { Length: > 0 } text)
                {
                    // tzdata.zi opens with "# version 2025b"; +VERSION holds the version alone.
                    return text.Replace("#", string.Empty, StringComparison.Ordinal)
                        .Replace("version", string.Empty, StringComparison.OrdinalIgnoreCase)
                        .Trim();
                }
            }

            if (zoneInfoRoot is not null)
            {
                return null;
            }
        }

        return OperatingSystem.IsWindows()
            ? VersionOfWindowsFile(Path.Combine(windowsDirectory ?? Environment.SystemDirectory, "tzres.dll"))
            : null;
    }

    private static string? MajorOfIcuLibrary(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        // libicuuc.so.74 and libicuuc.so.74.2 both name the data version a reader can act on.
        foreach (var file in Directory.EnumerateFiles(directory, "libicuuc.so*"))
        {
            var name = Path.GetFileName(file);
            var marker = name.LastIndexOf(".so.", StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }

            var version = name[(marker + 4)..];
            var dot = version.IndexOf('.', StringComparison.Ordinal);
            var major = dot < 0 ? version : version[..dot];
            if (major.Length > 0 && major.All(char.IsAsciiDigit))
            {
                return major;
            }
        }

        return null;
    }

    private static string? VersionOfWindowsFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    private static string? FirstLine(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            return reader.ReadLine();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static InvalidOperationException Missing(string what) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"The process cannot name the {what} version it prints with; a document whose globalisation data nobody can name is never recorded."));
}
