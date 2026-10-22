using System.Diagnostics;

namespace PdfAssemblySpike;

/// <summary>Result of one external process.</summary>
internal sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>
/// Starts external programs with an argument list (never through a shell) and
/// collects their output. Optionally calls a sampler at a fixed interval while
/// the program runs.
/// </summary>
internal static class ExternalTool
{
    public static ToolResult Run(string fileName, IEnumerable<string> arguments) =>
        Run(fileName, arguments, TimeSpan.FromHours(2), null, TimeSpan.FromMilliseconds(200));

    public static ToolResult Run(string fileName, IEnumerable<string> arguments, TimeSpan timeout, Action? sample, TimeSpan sampleInterval)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var clock = Stopwatch.StartNew();
        bool timedOut = false;
        sample?.Invoke();
        while (!process.WaitForExit(sampleInterval))
        {
            sample?.Invoke();
            if (clock.Elapsed > timeout)
            {
                timedOut = true;
                process.Kill(entireProcessTree: true);
                break;
            }
        }

        process.WaitForExit();
        sample?.Invoke();
        return new ToolResult(process.ExitCode, output.Result, error.Result, timedOut);
    }

    /// <summary>Runs a program and returns its standard output; throws when it fails.</summary>
    public static string Output(string fileName, params string[] arguments)
    {
        var result = Run(fileName, arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {string.Join(' ', arguments)} exited {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }
}
