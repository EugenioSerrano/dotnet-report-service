using System.Diagnostics;
using ReportService.Jobs;
using ReportService.Pdf.Verification;
using ReportService.Testing;
using ReportService.Testing.Containers;

namespace ReportService.EndToEndTests;

/// <summary>
/// Both halves of the restart evidence over the real composition: the merge worker's
/// deterministic assembly interruption (the seam exits it after the first batch) and
/// the render worker's real container stop after a block has committed. Each resumed
/// run must produce the same pages and the same global stamps as a clean run; after
/// the output ended, a further part commit is refused by the late-commit guard and
/// writes no row. Authored in the V-Bounce; run in the e2e cycle.
/// </summary>
public sealed class MultiBlockRenderMergeRestartTests
{
    private const string InterruptVariable = "REPORTSERVICE_ASSEMBLY_INTERRUPT_AFTER_BATCH";

    [Fact]
    public async Task ARestartDuringRenderOrAssemblyResumesToTheSameContent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var clean = await RunAsync(Restart.None, cancellationToken);
        var assemblyInterrupted = await RunAsync(Restart.Assembly, cancellationToken);
        var renderStopped = await RunAsync(Restart.Render, cancellationToken);

        Assert.Equal("SUCCEEDED", clean.Status);
        Assert.Equal("SUCCEEDED", assemblyInterrupted.Status);
        Assert.Equal("SUCCEEDED", renderStopped.Status);
        Assert.NotNull(clean.ArtifactId);
        Assert.NotNull(assemblyInterrupted.ArtifactId);
        Assert.NotNull(renderStopped.ArtifactId);

        Assert.Equal(clean.PageTexts, assemblyInterrupted.PageTexts);
        Assert.Equal(clean.Stamps, assemblyInterrupted.Stamps);
        Assert.Equal(clean.PageTexts, renderStopped.PageTexts);
        Assert.Equal(clean.Stamps, renderStopped.Stamps);
    }

    private static async Task<Run> RunAsync(Restart restart, CancellationToken cancellationToken)
    {
        var previous = Environment.GetEnvironmentVariable(InterruptVariable);
        Environment.SetEnvironmentVariable(InterruptVariable, restart == Restart.Assembly ? "1" : null);
        try
        {
            using var stage = new MultiBlockStage();
            await using var composition = await stage.StartAsync(cancellationToken);
            await stage.TakeTemplateAsync(composition, cancellationToken);
            var uploadId = await stage.SeedConfirmingUploadAsync(composition, cancellationToken);
            var reportJobId = await stage.SubmitAsync(composition, uploadId, cancellationToken);
            await stage.CommitPartitionAsync(composition, reportJobId, uploadId, cancellationToken);
            await stage.CompleteUploadAsync(composition, uploadId, cancellationToken);

            if (restart == Restart.Render)
            {
                await StopRenderWorkerAfterACommitAsync(stage, composition, reportJobId, cancellationToken);
            }

            if (restart == Restart.Assembly)
            {
                await ResumeMergeWorkerAfterInterruptionAsync(stage, composition, reportJobId, cancellationToken);
            }

            var ended = await stage.WaitForEndAsync(composition, reportJobId, cancellationToken);
            Assert.NotNull(ended.OutputId);
            Assert.NotNull(ended.ObjectKey);
            Assert.NotNull(ended.SizeBytes);
            Assert.NotNull(ended.Sha256);

            if (restart == Restart.Render)
            {
                // The late-commit guard, exercised in the covering run: once the output
                // ended, a further part commit is refused and writes no row.
                var jobs = new PostgresJobStore(composition.DataSource);
                var refused = await jobs.CommitPartAsync(
                    new PartCommit(
                        ended.OutputId!,
                        99,
                        "parts/" + reportJobId + "/99/000000000000000000000001",
                        new string('a', 64),
                        1,
                        1,
                        new string('b', 64),
                        DateTimeOffset.UtcNow),
                    cancellationToken);
                Assert.Equal(PartCommitOutcome.GuardRefused, refused.Outcome);
                Assert.Null(await jobs.FindPartAsync(ended.OutputId!, 99, cancellationToken));
            }

            var path = await stage.DownloadAsync(
                composition,
                ended.ObjectKey!,
                ended.SizeBytes!.Value,
                ended.Sha256!,
                restart switch
                {
                    Restart.Assembly => "assembly-interrupted.pdf",
                    Restart.Render => "render-stopped.pdf",
                    _ => "clean.pdf",
                },
                cancellationToken);
            var reading = PdfWordReader.Read(path, MultiBlockStage.Margins);
            var area = MultiBlockStage.Area;
            var texts = reading.Pages
                .Select(page => string.Join('|', MultiBlockStage.OutsideArea(page)))
                .ToList();
            var stamps = reading.Pages
                .Select(page => string.Join(
                    ' ',
                    page.Words.Where(word => MultiBlockStage.Inside(word, area)).Select(word => word.Text)))
                .ToList();
            return new Run(ended.Status, ended.ArtifactId, ended.OutputId, texts, stamps);
        }
        finally
        {
            Environment.SetEnvironmentVariable(InterruptVariable, previous);
        }
    }

    /// <summary>
    /// Waits for the assembly seam to fire, then makes sure the merge worker is running
    /// again: Docker's restart policy may already have replaced the container; when it
    /// has not, the same container is started, as the render half of the case does. The
    /// resumed process reads the one-shot marker and installs no seam, so the engine's
    /// retry assembles the committed parts and publishes the document.
    /// </summary>
    private static async Task ResumeMergeWorkerAfterInterruptionAsync(
        MultiBlockStage stage,
        MultiBlockStage.Composition composition,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        var container = await FindMergeWorkerAsync(composition, cancellationToken);
        var restartsBefore = await container.RestartCountAsync(cancellationToken);

        string? outputId = null;
        await Wait.UntilAsync(
            "the output to be recorded",
            async token =>
            {
                outputId ??= await stage.FindOutputIdAsync(composition, reportJobId, token);
                return outputId is not null;
            },
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

        var marker = MultiBlockStage.InterruptionMarker(outputId!);
        await Wait.UntilAsync(
            "the assembly interruption to fire",
            _ => Task.FromResult(File.Exists(marker)),
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

        // The marker is written before the process exits. Either the runtime restarts
        // the container (its restart count moves) or it does not (the container stops);
        // the case then starts the same container itself.
        await container.WaitUntilStoppedOrRestartedAsync(
            "the merge worker to stop or be replaced",
            restartsBefore,
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

        if (!await container.IsRunningAsync(cancellationToken))
        {
            await container.StartAsync(cancellationToken);
        }

        await container.WaitUntilRunningAsync(
            "the merge worker to run again",
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
    }

    /// <summary>
    /// Waits until one block of the output has committed, then stops the composition's
    /// render worker container and starts it again: the engine retries the interrupted
    /// render, which returns the committed part, and the output completes from committed
    /// work.
    /// </summary>
    private static async Task StopRenderWorkerAfterACommitAsync(
        MultiBlockStage stage,
        MultiBlockStage.Composition composition,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        string? outputId = null;
        await Wait.UntilAsync(
            "one block of the output to commit",
            async token =>
            {
                outputId ??= await stage.FindOutputIdAsync(composition, reportJobId, token);
                return outputId is not null
                    && (await stage.ReadPartsAsync(composition, outputId, token)).Count > 0;
            },
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

        var container = await FindRenderWorkerAsync(cancellationToken);
        await DockerAsync(["stop", container], cancellationToken);
        await Wait.UntilAsync(
            "the render worker container to stop",
            async token => !await IsRunningAsync(container, token),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

        await DockerAsync(["start", container], cancellationToken);
        await Wait.UntilAsync(
            "the render worker container to run again",
            token => IsRunningAsync(container, token),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
    }

    private static async Task<string> FindRenderWorkerAsync(CancellationToken cancellationToken)
    {
        var output = await DockerAsync(
            ["ps", "--filter", "name=render-worker", "--format", "{{.Names}}"],
            cancellationToken);
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(name => name.Contains("render-worker", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("the composition's render worker container was not found");
    }

    private static Task<MergeWorkerContainer> FindMergeWorkerAsync(
        MultiBlockStage.Composition composition,
        CancellationToken cancellationToken) =>
        MergeWorkerContainer.ResolveAsync(
            new DockerContainerRuntime(),
            composition.MergeWorkerContainerName,
            cancellationToken);

    private static async Task<bool> IsRunningAsync(string container, CancellationToken cancellationToken)
    {
        var state = await DockerAsync(
            ["inspect", "--format", "{{.State.Running}}", container],
            cancellationToken);
        return state.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> DockerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("docker could not be started");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {error}");
        }

        return output;
    }

    private enum Restart
    {
        None,
        Assembly,
        Render,
    }

    private sealed record Run(
        string Status,
        string? ArtifactId,
        string? OutputId,
        IReadOnlyList<string> PageTexts,
        IReadOnlyList<string> Stamps);
}
