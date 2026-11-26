using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReportService.Workers.Merge.Assembly;

/// <summary>
/// The deterministic interruption of one assembly, for the restart test only: the
/// worker process exits after a chosen staging batch, exactly where a real worker
/// loss would leave committed parts and no published output. The composition
/// installs it only when the process is told to; a normal run holds none.
/// <para>
/// The seam is one-shot per output. Before it exits it writes a marker under the
/// shared storage root the worker already mounts; a resumed attempt for the same
/// output sees the marker and installs no seam, so the retry assembles the committed
/// parts instead of exiting again. The marker is written only when the environment
/// names the seam, is one tiny file per interrupted output, and is namespaced by the
/// SHA-256 of the output id.
/// </para>
/// </summary>
internal sealed class AssemblyInterruption
{
    /// <summary>The variable that makes a merge worker install the seam.</summary>
    public const string AfterBatchVariable = "REPORTSERVICE_ASSEMBLY_INTERRUPT_AFTER_BATCH";

    /// <summary>The directory under the shared storage root the one-shot marker lives in.</summary>
    public const string MarkerDirectory = "assembly-interrupt";

    private readonly int _afterBatch;
    private readonly Action<int> _exit;
    private readonly string? _markerRoot;

    /// <summary>Creates the seam for the given batch.</summary>
    /// <param name="afterBatch">The batch after which the process exits, from one.</param>
    /// <param name="exit">The exit action; the production composition passes the process exit.</param>
    /// <param name="markerRoot">
    /// The shared root the one-shot marker is written under, or <see langword="null"/>
    /// when the seam has no shared location (a process that is restarted must then
    /// re-arm, which no composition with the variable set allows).
    /// </param>
    public AssemblyInterruption(int afterBatch, Action<int> exit, string? markerRoot = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(afterBatch, 1);
        ArgumentNullException.ThrowIfNull(exit);
        _afterBatch = afterBatch;
        _exit = exit;
        _markerRoot = string.IsNullOrWhiteSpace(markerRoot) ? null : markerRoot;
    }

    /// <summary>
    /// The seam a process installs from its environment, or none when it is not told
    /// to interrupt. The marker root is the storage root the worker mounts, so the
    /// marker survives the restart and the resumed process can see it.
    /// </summary>
    /// <param name="markerRoot">
    /// The shared storage root, when the caller already has it; when absent the
    /// process's own <c>Storage__Filesystem__Root</c> is read.
    /// </param>
    public static AssemblyInterruption? FromEnvironment(string? markerRoot = null)
    {
        var text = Environment.GetEnvironmentVariable(AfterBatchVariable);
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var batch) || batch < 1)
        {
            return null;
        }

        var root = string.IsNullOrWhiteSpace(markerRoot)
            ? Environment.GetEnvironmentVariable("Storage__Filesystem__Root")
            : markerRoot;
        return new AssemblyInterruption(batch, static code => Environment.Exit(code), root);
    }

    /// <summary>
    /// Whether an earlier attempt for <paramref name="outputId"/> already fired the
    /// seam. A resumed process asks this before arming, so the retry assembles.
    /// </summary>
    /// <param name="outputId">The output the assembly is for.</param>
    public bool IsConsumed(string outputId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputId);
        return MarkerFile(outputId) is { } path && File.Exists(path);
    }

    /// <summary>Writes the one-shot marker and exits the process when <paramref name="batch"/> is the chosen batch.</summary>
    /// <param name="batch">The batch just written, from one.</param>
    /// <param name="outputId">The output the assembly is for.</param>
    public void AfterBatch(int batch, string outputId)
    {
        if (batch != _afterBatch)
        {
            return;
        }

        if (MarkerFile(outputId) is { } path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, batch.ToString(CultureInfo.InvariantCulture));
        }

        _exit(1);
    }

    /// <summary>The marker file's name for one output: the SHA-256 of its id, lower-case hexadecimal.</summary>
    /// <param name="outputId">The output the assembly is for.</param>
    public static string MarkerName(string outputId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputId);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(outputId))) + ".marker";
    }

    private string? MarkerFile(string outputId) =>
        _markerRoot is null ? null : Path.Combine(_markerRoot, MarkerDirectory, MarkerName(outputId));
}
