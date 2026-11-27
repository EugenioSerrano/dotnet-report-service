using System.Text.Json;
using ReportService.Testing;
using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The decisions the window made before the continuation drain existed, recorded once over a
/// fixed set of windows and block counts. Executions recorded before the drain replay through
/// the legacy decisions, so those must stay exactly what this transcript holds.
/// </summary>
public sealed class PdfOutputWindowTranscript
{
    /// <summary>The variable that makes the opt-in case write the transcript instead of skipping.</summary>
    public const string RecordVariable = "REPORTSERVICE_RECORD_WINDOW_TRANSCRIPT";

    /// <summary>The windows the transcript covers.</summary>
    public static readonly int[] Windows = [1, 2, 3, 64];

    /// <summary>The block counts the transcript covers.</summary>
    public static readonly int[] BlockCounts = [1, 11, 1_000, 1_001, 2_500];

    /// <summary>Where the transcript is committed, from the repository root.</summary>
    /// <returns>The transcript's path.</returns>
    public static string PathOf() => Path.Combine(
        RepositoryPaths.Root,
        "tests",
        "ReportService.Workers.Orchestration.Tests",
        "Jobs",
        "PdfOutputWindowLegacyTranscript.json");

    /// <summary>The key one case is stored under.</summary>
    /// <param name="window">The window.</param>
    /// <param name="blockCount">The block count.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(int window, int blockCount) => $"window={window};blocks={blockCount}";

    /// <summary>Reads the committed transcript.</summary>
    /// <returns>The recorded decisions by case.</returns>
    public static IReadOnlyDictionary<string, string> Read() =>
        JsonSerializer.Deserialize<SortedDictionary<string, string>>(File.ReadAllText(PathOf()))
            ?? throw new InvalidOperationException("the transcript is empty.");

    [Fact]
    public void RecordTheDecisionsOfTheCurrentWindow()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(RecordVariable) == "1",
            $"Opt-in recording: set {RecordVariable}=1 to write the transcript.");

        var transcript = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var window in Windows)
        {
            foreach (var blockCount in BlockCounts)
            {
                transcript[KeyOf(window, blockCount)] =
                    PdfOutputWindowLoop.RunChain(PdfOutputWindow.Decide, blockCount, window).Trace;
            }
        }

        File.WriteAllText(
            PathOf(),
            JsonSerializer.Serialize(transcript, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
