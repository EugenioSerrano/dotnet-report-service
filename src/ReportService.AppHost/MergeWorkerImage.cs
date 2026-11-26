using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ReportService.AppHost;

/// <summary>
/// Prepares the merge worker image for the local composition: the .NET SDK
/// publishes the merge worker's entry project on the platform's ASP.NET Core
/// runtime image into the local container runtime. The image carries no browser
/// and no PDF base; it is prepared once per run, like the render worker's.
/// </summary>
internal sealed record MergeWorkerImage(
    string RepositoryRoot,
    string WorkerProject,
    string Repository,
    string Tag)
{
    /// <summary>The variable that tells a composition the run has already prepared the merge worker image.</summary>
    public const string PreparedVariable = "REPORTSERVICE_MERGE_WORKER_IMAGE_PREPARED";

    /// <summary>The merge worker's entry project file under a repository root.</summary>
    internal static string WorkerProjectPath(string repositoryRoot) => Path.Combine(
        repositoryRoot, "src", "ReportService.Workers.Merge.Entry", "ReportService.Workers.Merge.Entry.csproj");

    /// <summary>Reads the image names from the merge worker's entry project file.</summary>
    public static async Task<MergeWorkerImage> DescribeAsync(
        string appHostDirectory, CancellationToken cancellationToken = default)
    {
        var repositoryRoot = RenderWorkerImage.ResolveRepositoryRoot(appHostDirectory);
        var workerProject = WorkerProjectPath(repositoryRoot);
        var output = await RunAsync(
            repositoryRoot,
            "dotnet",
            ["msbuild", workerProject, "-getProperty:ContainerRepository", "-getProperty:ContainerImageTag"],
            logger: null,
            cancellationToken);
        using var json = JsonDocument.Parse(output);
        var properties = json.RootElement.GetProperty("Properties");
        string Read(string name) => properties.GetProperty(name).GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"The merge worker's entry project does not define {name}.");
        return new MergeWorkerImage(repositoryRoot, workerProject, Read("ContainerRepository"), Read("ContainerImageTag"));
    }

    /// <summary>Publishes the merge worker image into the local container runtime.</summary>
    public static async Task PrepareAsync(
        MergeWorkerImage image, IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ReportService.AppHost.MergeWorkerImage");
        logger.LogInformation("Publishing the merge worker image {Image}:{Tag}", image.Repository, image.Tag);
        await RunAsync(
            image.RepositoryRoot,
            "dotnet",
            ["publish", image.WorkerProject, "-c", "Release", "-t:PublishContainer"],
            logger,
            cancellationToken);
    }

    /// <summary>Uses the image the run prepared, without publishing it a second time.</summary>
    public static async Task UsePreparedAsync(
        MergeWorkerImage image, IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ReportService.AppHost.MergeWorkerImage");
        try
        {
            await RunAsync(
                image.RepositoryRoot,
                "docker",
                ["image", "inspect", "--format", "{{.Id}}", $"{image.Repository}:{image.Tag}"],
                logger: null,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"The run reports the merge worker image {image.Repository}:{image.Tag} as prepared, but the "
                + "container runtime does not hold it. Run the test level's command, which prepares it once "
                + "before the tests start.",
                exception);
        }

        logger.LogInformation(
            "Using the merge worker image {Image}:{Tag} prepared for this run", image.Repository, image.Tag);
    }

    private static async Task<string> RunAsync(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = new StringBuilder();
        var error = new StringBuilder();
        void Collect(StringBuilder target, string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (target)
            {
                target.AppendLine(line);
            }

            logger?.LogInformation("{Command}: {Line}", fileName, line);
        }

        process.OutputDataReceived += (_, e) => Collect(output, e.Data);
        process.ErrorDataReceived += (_, e) => Collect(error, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // The process has already exited, or cannot be stopped any more.
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{error}");
        }

        return output.ToString();
    }
}
