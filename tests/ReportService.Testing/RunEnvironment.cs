using System.Globalization;

namespace ReportService.Testing;

/// <summary>
/// The folders a test run gives its classes: the run's own evidence folder, named by the
/// runner in an environment variable, and the evidence subfolder a class writes an artefact
/// into. The variable's name is the runner's too (<c>eng/test/ReportService.Eng.TestRunner</c>
/// writes it for every level); a class that must leave a document, a snapshot or a report for
/// a person to inspect uses these members instead of guessing a path.
/// </summary>
public static class RunEnvironment
{
    /// <summary>The variable that names the run's evidence folder to a test process.</summary>
    public const string RunDirectoryVariable = "REPORTSERVICE_RUN_DIRECTORY";

    /// <summary>The subfolder of the run's evidence folder that artefacts are written into.</summary>
    public const string EvidenceFolderName = "evidence";

    /// <summary>
    /// The run's evidence folder, created if it does not exist, or <see langword="null"/> when
    /// the process was started outside the runner — a class run by hand has no run folder, and
    /// a case that needs one says so instead of writing into a working directory.
    /// </summary>
    public static string? RunDirectory
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(RunDirectoryVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    /// <summary>
    /// The evidence folder of the run, created if it does not exist.
    /// </summary>
    /// <returns>The absolute path of the folder.</returns>
    /// <exception cref="InvalidOperationException">The process was not started by the runner.</exception>
    public static string EvidenceDirectory()
    {
        var run = RunDirectory
            ?? throw new InvalidOperationException(
                $"The process was started without {RunDirectoryVariable}, so it has no run folder to write evidence into. Run the level through eng/test/ReportService.Eng.TestRunner.");
        var evidence = Path.Combine(run, EvidenceFolderName);
        Directory.CreateDirectory(evidence);
        return evidence;
    }

    /// <summary>Writes <paramref name="content"/> to the run's evidence folder and answers its path.</summary>
    /// <param name="fileName">The file's name inside the evidence folder.</param>
    /// <param name="content">The bytes to write.</param>
    /// <returns>The absolute path of the file written.</returns>
    public static string WriteEvidence(string fileName, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var path = Path.Combine(EvidenceDirectory(), fileName);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        file.Write(content);
        return path;
    }

    /// <summary>The SHA-256 of <paramref name="content"/>, lower-case hexadecimal.</summary>
    /// <param name="content">The bytes.</param>
    public static string Sha256Of(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

    /// <summary>The stamp of a run folder's name, for a line of evidence that names the run.</summary>
    public static string Stamp() =>
        DateTimeOffset.Now.ToString("yyMMdd-HHmm", CultureInfo.InvariantCulture);
}
