using System.Diagnostics;
using System.Text;

namespace PageAccountingCheck;

/// <summary>Exit code, standard output (or the file it was written to) and bounded standard error of one tool call.</summary>
internal sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs a command-line tool (qpdf, pdftoppm) as a child process with a deadline:
/// on expiry or cancellation the child is killed and a TimeoutException is thrown.
/// Standard error is kept up to 64 KiB; standard output either the same way or,
/// for large outputs, streamed to a file.
/// </summary>
internal static class Tool
{
    private const int MaxCapturedChars = 64 * 1024;

    public static async Task<ToolResult> RunAsync(string tool, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellation, string? stdoutFile = null)
    {
        var info = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {tool}.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        Task<string> stdout;
        if (stdoutFile is null)
        {
            stdout = ReadBoundedAsync(process.StandardOutput);
        }
        else
        {
            stdout = CopyToFileAsync(process.StandardOutput, stdoutFile);
        }

        var stderr = ReadBoundedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException($"{tool} did not finish within {timeout.TotalSeconds:0} s.");
        }

        return new ToolResult(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<string> CopyToFileAsync(StreamReader reader, string path)
    {
        await using var file = File.Create(path);
        await reader.BaseStream.CopyToAsync(file);
        return path;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var kept = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (kept.Length < MaxCapturedChars)
            {
                kept.Append(buffer, 0, Math.Min(read, MaxCapturedChars - kept.Length));
            }
        }

        return kept.ToString();
    }
}
