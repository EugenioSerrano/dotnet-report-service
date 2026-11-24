using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ReportService.AppHost;

namespace ReportService.AppHost.Tests;

/// <summary>
/// A deterministic, hermetic reproduction of the AppHost docker-engine probe's
/// child-stream drain deadlock (BUG-055). A child (the flooder) writes well over an
/// OS pipe buffer to standard error before it writes any standard output, and is
/// drained through the <b>real</b> product seam
/// <see cref="SharedStorageRoot.DrainToExitAsync(Process, System.Threading.CancellationToken)"/>
/// — the method the probe (<c>IsDockerDesktopAsync</c>) routes through. The seam
/// starts no process and kills none: the test owns the child and imposes its own
/// bounded timeout, killing the child tree and observing the abandoned drain so no
/// process is left running and no task fault is unobserved.
///
/// The oracle is the approved intent (SPEC-261124-0146 AC-1 / Phase C.1): the drain
/// must complete within the bounded timeout and return the full captured content and
/// the child's exit code. Against the current sequential seam body (standard output
/// read to completion before standard error) this deadlocks: the child blocks writing
/// standard error once its pipe buffer fills, the parent blocks awaiting the end of a
/// standard output the child cannot flush, and the timeout fires — the test fails
/// (RED). Against the concurrent drain the same test passes (GREEN).
/// </summary>
public sealed class ChildProcessStreamDrainProbeTests
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    private static readonly string FlooderDll =
        typeof(ChildProcessStreamDrainProbeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "StreamFlooderDll")
            .Value
        ?? throw new InvalidOperationException("The StreamFlooderDll assembly metadata is missing or empty.");

    [Fact]
    public async Task DrainsAChildThatFloodsStandardErrorBeyondThePipeBufferWithoutDeadlocking()
    {
        // 10 MiB to standard error, written before any standard output, is comfortably
        // above the Windows .NET default and the Linux 64 KiB anonymous-pipe buffer, so
        // a sequential drain (standard output first) deadlocks deterministically.
        const int stdErrBytes = 10 * 1024 * 1024;
        const int stdOutBytes = 1024;

        var cancellationToken = TestContext.Current.CancellationToken;
        using var process = StartFlooder(stdErrBytes, stdOutBytes, exitCode: 0);
        var drain = SharedStorageRoot.DrainToExitAsync(process, cancellationToken);

        var finished = await Task.WhenAny(drain, Task.Delay(DrainTimeout, cancellationToken));
        if (finished != drain)
        {
            // Deadlock: leave no hung process and no unobserved drain-task fault.
            process.Kill(entireProcessTree: true);
            await Observe(drain);
            Assert.Fail(
                $"The probe drain did not complete within {DrainTimeout.TotalSeconds:F0}s — "
                + "the child-process stream drain deadlocked (standard error flooded past the "
                + "pipe buffer while the sequential drain awaited the end of standard output).");
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
        var drain = SharedStorageRoot.DrainToExitAsync(process, cancellationToken);

        var finished = await Task.WhenAny(drain, Task.Delay(DrainTimeout, cancellationToken));
        Assert.True(finished == drain, "the small-output probe drain did not complete within the timeout.");

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
        var drain = SharedStorageRoot.DrainToExitAsync(process, cancellationToken);

        var finished = await Task.WhenAny(drain, Task.Delay(DrainTimeout, cancellationToken));
        Assert.True(finished == drain, "the non-zero-exit probe drain did not complete within the timeout.");

        var (stdOut, stdErr) = await drain;

        // The seam returns the two captured strings and never throws on a non-zero exit;
        // the exit decision stays with the caller (IsDockerDesktopAsync reads ExitCode == 0),
        // so the probe's result contract is unaffected by the extraction.
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
