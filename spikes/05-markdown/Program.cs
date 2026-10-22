using System.Text;
using System.Text.Json;
using MarkdownSpike.Checking;
using MarkdownSpike.Measuring;
using MarkdownSpike.Routes;

namespace MarkdownSpike;

/// <summary>
/// Markdown conversion spike: converts the grouped pilot dataset to Markdown
/// through four routes on one harness and measures table continuity, memory
/// and losses.
/// </summary>
internal static class Program
{
    private const string Usage = """
        Usage:
          inventory [--template DIR [--data FILE]]
          probe
          selftest
          convert   --route html|a|b|c|d --data FILE --output FILE [--template DIR] [--md-template DIR]
                    [--block-size N] [--whole-threshold N] [--force-whole] [--temp DIR]
          check     --data FILE --output FILE [--block-size N] [--subtotal-rows N] [--heading-rows N]
                    [--tables per-group|per-document]
          measure   --data-dir DIR --work DIR [--sizes 1k,10k,100k,500k] [--runs 3] [--routes html,a,b,c,d]
                    [--block-size 48] [--timeout-minutes 20] [--results DIR] [--no-extras]
          samples
          summarize [--results DIR]
        Routes: html = baseline (HTML written unchanged), a = library per fragment with table stitching,
                b = own streaming converter, c = Markdown template parts, d = hybrid by size.
        Exit codes: 0 success, 1 failure, 2 invalid arguments.
        """;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            CommandLine line = CommandLine.Parse(args);
            return line.Command switch
            {
                "inventory" => HtmlInventory.Run(line),
                "probe" => ReverseMarkdownProbe.Run(line),
                "selftest" => SelfTest.Run(line),
                "convert" => ConvertCommand.Run(line),
                "check" => ContinuityChecker.Run(line),
                "measure" => MeasurementRunner.Run(line),
                "samples" => SampleOutputs.Run(line),
                "summarize" => ResultSummary.Run(line),
                _ => throw new UsageException($"Unknown command '{line.Command}'."),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
