using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ReportService.Testing;

namespace ReportService.Fixtures.Tests;

/// <summary>
/// A deterministic, hermetic reproduction of the child-stream drain deadlock: a child
/// (the flooder) that writes well over an OS pipe buffer to standard error before it
/// writes standard output. Drained through the real shared helper, it must complete
/// within a bounded timeout. Against a sequential drain (standard output to completion
/// before standard error) this deadlocks and the bounded timeout fires; against the
/// concurrent drain it completes immediately. The timeout path kills the child tree and
/// observes the abandoned drain so no process is left running and no task fault is
/// unobserved.
/// </summary>
public sealed class ChildProcessStreamDrainTests
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    private static readonly string FlooderDll =
        typeof(ChildProcessStreamDrainTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "StreamFlooderDll")
            .Value
        ?? throw new InvalidOperationException("The StreamFlooderDll assembly metadata is missing or empty.");

    [Fact]
    public async Task DrainsAChildThatFloodsStandardErrorBeyondThePipeBufferWithoutDeadlocking()
    {
        const int stdErrBytes = 10 * 1024 * 1024;
        const int stdOutBytes = 1024;

        var cancellationToken = TestContext.Current.CancellationToken;
        using var process = StartFlooder(stdErrBytes, stdOutBytes, exitCode: 0);
        var drain = ChildProcessStreams.DrainToExitAsync(process, cancellationToken);

        var finished = await Task.WhenAny(drain, Task.Delay(DrainTimeout, cancellationToken));
        if (finished != drain)
        {
            // Deadlock: leave no hung process and no unobserved drain-task fault.
            process.Kill(entireProcessTree: true);
            await Observe(drain);
            Assert.Fail(
                $"The drain did not complete within {DrainTimeout.TotalSeconds:F0}s — "
                + "the child-process stream drain deadlocked.");
        }

        var (stdOut, stdErr) = await drain;
        Assert.Equal(stdOutBytes, stdOut.Length);
        Assert.Equal(stdErrBytes, stdErr.Length);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task CapturesBothStreamsContentForASmallChildWithinTheTimeout()
    {
        const int stdErrBytes = 2048;
        const int stdOutBytes = 4096;

        var cancellationToken = TestContext.Current.CancellationToken;
        using var process = StartFlooder(stdErrBytes, stdOutBytes, exitCode: 0);
        var drain = ChildProcessStreams.DrainToExitAsync(process, cancellationToken);

        var finished = await Task.WhenAny(drain, Task.Delay(DrainTimeout, cancellationToken));
        Assert.True(finished == drain, "the small-output drain did not complete within the timeout.");

        var (stdOut, stdErr) = await drain;
        Assert.Equal(new string('O', stdOutBytes), stdOut);
        Assert.Equal(new string('E', stdErrBytes), stdErr);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task DoesNotSurfaceANonZeroExitCodeTheCallerReadsItFromTheProcess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var process = StartFlooder(stdErrBytes: 1024, stdOutBytes: 1024, exitCode: 3);
        var drain = ChildProcessStreams.DrainToExitAsync(process, cancellationToken);

        var finished = await Task.WhenAny(drain, Task.Delay(DrainTimeout, cancellationToken));
        Assert.True(finished == drain, "the non-zero-exit drain did not complete within the timeout.");

        var (stdOut, stdErr) = await drain;

        // The helper returns the two captured strings and never throws on a non-zero exit;
        // the exit decision stays with the caller (RunAsync throws, TryEvaluate returns null),
        // so both affected sites' exit handling is unaffected by the extraction.
        Assert.Equal(1024, stdOut.Length);
        Assert.Equal(1024, stdErr.Length);
        Assert.Equal(3, process.ExitCode);
    }

    private static Process StartFlooder(int stdErrBytes, int stdOutBytes, int exitCode)
    {
        Assert.True(File.Exists(FlooderDll), $"The flooder binary was not found at {FlooderDll}.");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(FlooderDll);
        startInfo.ArgumentList.Add("--stderr-bytes");
        startInfo.ArgumentList.Add(stdErrBytes.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--stdout-bytes");
        startInfo.ArgumentList.Add(stdOutBytes.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--exit-code");
        startInfo.ArgumentList.Add(exitCode.ToString(CultureInfo.InvariantCulture));

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the flooder.");
    }

    private static async Task Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The drain against a killed child may fault or complete with partial content;
            // awaiting it here observes the task so it never faults unobserved.
        }
    }
}
