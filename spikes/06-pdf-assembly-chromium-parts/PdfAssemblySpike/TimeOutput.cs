using System.Globalization;

namespace PdfAssemblySpike;

/// <summary>
/// The figures GNU time ("/usr/bin/time -v") writes for one finished process:
/// peak resident set size, wall-clock and CPU times, exit status and the
/// terminating signal when the process was killed.
/// </summary>
internal sealed record TimeOutput(long MaxRssKb, double WallSeconds, double UserSeconds, double SystemSeconds, int? ExitStatus, int? Signal)
{
    public const string MaxRssLabel = "Maximum resident set size (kbytes)";
    public const string WallLabel = "Elapsed (wall clock) time";

    public static TimeOutput Parse(string path)
    {
        long rss = 0;
        double wall = 0;
        double user = 0;
        double system = 0;
        int? exit = null;
        int? signal = null;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("Command terminated by signal", StringComparison.Ordinal))
            {
                signal = int.Parse(line["Command terminated by signal".Length..].Trim(), CultureInfo.InvariantCulture);
                continue;
            }

            int colon = line.LastIndexOf(": ", StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            string label = line[..colon];
            string value = line[(colon + 2)..].Trim();
            if (label.StartsWith(MaxRssLabel, StringComparison.Ordinal))
            {
                rss = long.Parse(value, CultureInfo.InvariantCulture);
            }
            else if (label.StartsWith(WallLabel, StringComparison.Ordinal))
            {
                wall = ParseClock(value);
            }
            else if (label.StartsWith("User time (seconds)", StringComparison.Ordinal))
            {
                user = double.Parse(value, CultureInfo.InvariantCulture);
            }
            else if (label.StartsWith("System time (seconds)", StringComparison.Ordinal))
            {
                system = double.Parse(value, CultureInfo.InvariantCulture);
            }
            else if (label.StartsWith("Exit status", StringComparison.Ordinal))
            {
                exit = int.Parse(value, CultureInfo.InvariantCulture);
            }
        }

        return new TimeOutput(rss, wall, user, system, exit, signal);
    }

    /// <summary>Parses "m:ss.ss" or "h:mm:ss".</summary>
    public static double ParseClock(string value)
    {
        string[] parts = value.Split(':');
        double seconds = 0;
        foreach (string part in parts)
        {
            seconds = (seconds * 60) + double.Parse(part, CultureInfo.InvariantCulture);
        }

        return seconds;
    }
}
