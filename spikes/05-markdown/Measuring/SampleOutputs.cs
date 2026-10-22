using System.Text;
using System.Text.Json;
using MarkdownSpike.Checking;
using MarkdownSpike.Routes;

namespace MarkdownSpike.Measuring;

/// <summary>
/// Writes the output of every route for the two sample packages' sample data
/// (block size 4, so the 40 grouped records arrive in 10 fragments) into
/// results/samples/, checks each, and saves the own converter's loss counts.
/// </summary>
internal static class SampleOutputs
{
    private const int SampleBlockSize = 4;

    public static int Run(CommandLine line)
    {
        line.Allow();
        string folder = Path.Combine(RepositoryPaths.Results, "samples");
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(Path.GetTempPath(), "markdown-spike-samples-" + Environment.ProcessId);
        var summary = new StringBuilder();
        foreach (string package in new[] { "pilot-grouped", "pilot-simple" })
        {
            string data = Path.Combine(RepositoryPaths.HtmlTemplate(package), "sample.json");
            foreach (string route in new[] { "html", "a", "b", "c", "d" })
            {
                string file = Path.Combine(folder, route == "html" ? $"{package}.html" : $"{package}.route-{route}.md");
                var options = new ConvertOptions(route, data, null, file, null, SampleBlockSize, HybridBySizeConverter.DefaultWholeThreshold, false, temp);
                ConvertResult result = ConvertCommand.Convert(options, out IReadOnlyDictionary<string, long>? losses);
                summary.Append(package).Append(" route ").Append(route).Append(": ").Append(result.Outcome)
                    .Append(" records=").Append(result.Records).Append(" blocks=").Append(result.Blocks)
                    .Append(result.Branch is null ? string.Empty : " branch=" + result.Branch).Append(result.Error is null ? string.Empty : " error=" + result.Error);
                if (route != "html" && result.Outcome == "ok")
                {
                    using var markdown = new StreamReader(file, Encoding.UTF8);
                    CheckResult check = ContinuityChecker.Check(data, markdown, SampleBlockSize, null, null, null);
                    summary.Append(" check=").Append(JsonSerializer.Serialize(check, ResultJson.Options));
                }

                summary.Append('\n');
                if (losses is not null && route == "b")
                {
                    var text = new StringBuilder();
                    foreach ((string what, long count) in losses)
                    {
                        text.Append(what).Append('\t').Append(count).Append('\n');
                    }

                    File.WriteAllText(Path.Combine(folder, $"{package}.route-b.losses.txt"), text.ToString(), new UTF8Encoding(false));
                }
            }
        }

        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, recursive: true);
        }

        File.WriteAllText(Path.Combine(folder, "checks.txt"), summary.ToString(), new UTF8Encoding(false));
        Console.Write(summary);
        return 0;
    }
}
