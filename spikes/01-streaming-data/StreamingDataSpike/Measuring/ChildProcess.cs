using System.Diagnostics;

namespace StreamingDataSpike.Measuring;

/// <summary>Runs this program again in a separate process, so that each measured run has its own peak memory.</summary>
internal static class ChildProcess
{
    /// <summary>Runs one command and captures its output.</summary>
    public static async Task<ChildResult> RunAsync(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };

        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown.");
        start.FileName = processPath;
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(ChildProcess).Assembly.Location);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The child process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new ChildResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}

/// <summary>Exit code and captured output of a child run.</summary>
internal sealed record ChildResult(int ExitCode, string StandardOutput, string StandardError);
