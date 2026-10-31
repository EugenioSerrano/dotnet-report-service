using System.Globalization;

namespace RowEndSweep;

/// <summary>One planned run: one fixture printed in one markup, one repetition, inside a series (round, repetition, markup, content).</summary>
internal sealed record SweepRun(string Run, string FixtureId, string Content, int Kib, string Markup, int Repetition, int Round, string Series, int SeriesIndex);

/// <summary>
/// The sweep: record 50 of an ordinary 100-record block at sizes from 64 KiB to
/// 144 KiB in 2 KiB steps, in two contents ("uniq", the shared X series' own text,
/// and "rep", record 50's own description repeated), in table and grid markup,
/// amount strategy "cell", three repetitions in repetition-major order.
/// </summary>
internal static class SweepPlan
{
    public const string Uniq = "uniq";
    public const string Rep = "rep";
    public const string Table = "table";
    public const string Grid = "grid";
    public const string Cell = "cell";
    public const int Repetitions = 3;

    public static readonly string[] Markups = [Table, Grid];
    public static readonly string[] Contents = [Uniq, Rep];

    /// <summary>The sizes of the first round, in KiB: 64, 66, …, 144.</summary>
    public static IReadOnlyList<int> FirstRoundKib { get; } = [.. Enumerable.Range(0, 41).Select(k => 64 + (2 * k))];

    /// <summary>The fixture id of a content and size: the shared X64K and X128K keep their ids.</summary>
    public static string Id(string content, int kib) => content == Uniq
        ? kib switch
        {
            64 => "X64K",
            128 => "X128K",
            _ => "U" + kib.ToString("D3", CultureInfo.InvariantCulture),
        }
        : "R" + kib.ToString("D3", CultureInfo.InvariantCulture);

    public static (string Content, int Kib) Parse(string id) => id switch
    {
        "X64K" => (Uniq, 64),
        "X128K" => (Uniq, 128),
        _ when id.Length == 4 && id[0] is 'U' or 'R' && int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var kib)
            => (id[0] == 'U' ? Uniq : Rep, kib),
        _ => throw new ArgumentException($"Not a sweep fixture id: '{id}'.", nameof(id)),
    };

    public static int RecordBytes(int kib) => kib * 1024;

    /// <summary>The run id of the earlier two-pass experiment's form: fixture, markup, "c" for strategy cell, repetition.</summary>
    public static string RunId(string fixture, string markup, int repetition) =>
        string.Create(CultureInfo.InvariantCulture, $"{fixture}-{markup}-c-r{repetition}");

    public static int RoundNumber(string round) => round switch
    {
        "smoke" => 0,
        "1" => 1,
        "2" => 2,
        _ => throw new ArgumentException($"Unknown round '{round}'.", nameof(round)),
    };

    /// <summary>The fixtures of the first round, both contents.</summary>
    public static List<string> FirstRoundIds() => [.. Contents.SelectMany(c => FirstRoundKib.Select(k => Id(c, k)))];

    /// <summary>
    /// The plan of a round. Smoke: X64K, X128K, U100, R100 in both markups, once (a
    /// development loop, never evidence). Round 1: every first-round fixture. Round 2:
    /// the listed fixtures. Rounds 1 and 2 run three repetitions, repetition-major:
    /// for each repetition, table then grid; in each markup "uniq" then "rep"; sizes ascending.
    /// </summary>
    public static List<SweepRun> Build(string round, IReadOnlyList<string>? extra)
    {
        var number = RoundNumber(round);
        List<string> ids = number switch
        {
            0 => ["X64K", "X128K", "U100", "R100"],
            1 => FirstRoundIds(),
            _ => [.. extra ?? throw new ArgumentException("Round 2 needs the list of its fixtures.", nameof(extra))],
        };
        var repetitions = number == 0 ? 1 : Repetitions;
        var parsed = ids.Distinct(StringComparer.Ordinal).Select(id => (Id: id, Parsed: Parse(id))).ToList();
        var runs = new List<SweepRun>();
        for (var r = 1; r <= repetitions; r++)
        {
            foreach (var markup in Markups)
            {
                foreach (var content in Contents)
                {
                    var series = $"{round}/r{r}/{markup}/{content}";
                    var index = 0;
                    foreach (var (id, (_, kib)) in parsed.Where(p => p.Parsed.Content == content).OrderBy(p => p.Parsed.Kib))
                    {
                        runs.Add(new SweepRun(RunId(id, markup, r), id, content, kib, markup, r, number, series, index++));
                    }
                }
            }
        }

        return runs;
    }
}
