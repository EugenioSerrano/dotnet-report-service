using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ReportService.AppHost;

/// <summary>
/// Prepares the render worker image for the local composition: a local,
/// loopback-only registry holds the PDF base image (built once per base tag),
/// and the worker image is published by the .NET SDK on that base and loaded
/// into the local container runtime.
/// </summary>
internal sealed record RenderWorkerImage(
    string RepositoryRoot,
    string WorkerProject,
    string Repository,
    string Tag,
    string BaseRegistry,
    string BaseRepository,
    string BaseTag)
{
    // Development-only registry: pinned image, bound to 127.0.0.1 only.
    private const string RegistryContainerName = "report-service-dev-registry";
    private const string RegistryImage =
        "registry:3.1.2@sha256:ddf754342cfc8acc51a56d5d0ab6af06826461864460636d8bd5c546dab2a7b8";
    private const string RegistryContainerPort = "5000/tcp";
    private const string LoopbackAddress = "127.0.0.1";
    private const string BaseDockerfile = "eng/pdf-base/Dockerfile";

    private static readonly string[] ManifestMediaTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    /// <summary>Reads the image names from the worker's project file.</summary>
    public static async Task<RenderWorkerImage> DescribeAsync(
        string appHostDirectory, CancellationToken cancellationToken = default)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(appHostDirectory, "..", ".."));
        var workerProject = Path.Combine(
            repositoryRoot, "src", "ReportService.Workers.Pdf", "ReportService.Workers.Pdf.csproj");
        var output = await RunAsync(
            repositoryRoot,
            "dotnet",
            ["msbuild", workerProject, "-getProperty:ContainerRepository", "-getProperty:ContainerImageTag",
             "-getProperty:PdfBaseImageRegistry", "-getProperty:PdfBaseImageRepository", "-getProperty:PdfBaseImageTag"],
            logger: null,
            cancellationToken);
        using var json = JsonDocument.Parse(output);
        var properties = json.RootElement.GetProperty("Properties");
        string Read(string name) => properties.GetProperty(name).GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"The worker project does not define {name}.");
        return new RenderWorkerImage(
            repositoryRoot,
            workerProject,
            Read("ContainerRepository"),
            Read("ContainerImageTag"),
            Read("PdfBaseImageRegistry"),
            Read("PdfBaseImageRepository"),
            Read("PdfBaseImageTag"));
    }

    /// <summary>Runs, in order: registry, base image (if missing), digest, worker publish.</summary>
    public static async Task PrepareAsync(
        RenderWorkerImage image, IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ReportService.AppHost.RenderWorkerImage");
        using var http = new HttpClient { BaseAddress = new Uri($"http://{image.BaseRegistry}/") };
        foreach (var mediaType in ManifestMediaTypes)
        {
            http.DefaultRequestHeaders.Accept.ParseAdd(mediaType);
        }

        await EnsureRegistryAsync(image, http, logger, cancellationToken);

        var baseReference = $"{image.BaseRegistry}/{image.BaseRepository}:{image.BaseTag}";
        var digest = await GetDigestAsync(image, http, cancellationToken);
        if (digest is null)
        {
            logger.LogInformation("Building the PDF base image {BaseImage} (first run only; takes a few minutes)", baseReference);
            await RunAsync(
                image.RepositoryRoot,
                "docker",
                ["buildx", "build", "--progress=plain", "--provenance=false", "-f", BaseDockerfile,
                 "-t", baseReference, "--push", "."],
                logger,
                cancellationToken);
            digest = await GetDigestAsync(image, http, cancellationToken)
                ?? throw new InvalidOperationException($"{baseReference} is missing from the local registry after the build.");
        }

        logger.LogInformation("PDF base image {BaseImage}@{Digest}", baseReference, digest);
        logger.LogInformation("Publishing the render worker image {Image}:{Tag}", image.Repository, image.Tag);
        await RunAsync(
            image.RepositoryRoot,
            "dotnet",
            ["publish", image.WorkerProject, "-c", "Release", "-t:PublishContainer", $"-p:PdfBaseImageDigest={digest}"],
            logger,
            cancellationToken);
    }

    private static async Task EnsureRegistryAsync(
        RenderWorkerImage image, HttpClient http, ILogger logger, CancellationToken cancellationToken)
    {
        var port = image.BaseRegistry.Split(':')[1];
        string? inspection;
        try
        {
            inspection = await RunAsync(
                image.RepositoryRoot,
                "docker",
                ["container", "inspect", RegistryContainerName],
                logger: null,
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            inspection = null;
        }

        if (inspection is null)
        {
            logger.LogInformation("Starting the local development registry {Container} on {Registry}", RegistryContainerName, image.BaseRegistry);
            await RunAsync(
                image.RepositoryRoot,
                "docker",
                ["run", "--detach", "--name", RegistryContainerName, "--publish", $"{LoopbackAddress}:{port}:5000",
                 "--env", "OTEL_TRACES_EXPORTER=none", RegistryImage],
                logger,
                cancellationToken);
        }
        else
        {
            var (containerImage, running, loopbackOnly) = ReadRegistryState(inspection, port);
            if (!string.Equals(containerImage, RegistryImage, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The container {RegistryContainerName} does not run {RegistryImage} (it runs {containerImage}). Remove it by name (docker rm -f {RegistryContainerName}) and start again.");
            }

            if (!loopbackOnly)
            {
                throw new InvalidOperationException(
                    $"The container {RegistryContainerName} is not published only on {LoopbackAddress}:{port}. Remove it by name (docker rm -f {RegistryContainerName}) and start again.");
            }

            if (running)
            {
                logger.LogInformation("Using the local development registry {Container} on {Registry}", RegistryContainerName, image.BaseRegistry);
            }
            else
            {
                logger.LogInformation("Restarting the local development registry {Container} on {Registry}", RegistryContainerName, image.BaseRegistry);
                await RunAsync(image.RepositoryRoot, "docker", ["start", RegistryContainerName], logger, cancellationToken);
            }
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(new Uri("v2/", UriKind.Relative), cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (attempt < 30)
            {
            }

            if (attempt >= 30)
            {
                throw new InvalidOperationException($"The local development registry on {image.BaseRegistry} does not answer.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    // Reads the image, the running state and whether the registry port is published on the loopback address only.
    private static (string Image, bool Running, bool LoopbackOnly) ReadRegistryState(string inspection, string port)
    {
        using var json = JsonDocument.Parse(inspection);
        var container = json.RootElement[0];
        var containerImage = container.GetProperty("Config").GetProperty("Image").GetString() ?? string.Empty;
        var running = container.GetProperty("State").GetProperty("Running").GetBoolean();

        var loopbackOnly = false;
        if (container.GetProperty("HostConfig").GetProperty("PortBindings") is { ValueKind: JsonValueKind.Object } bindings)
        {
            var published = bindings.EnumerateObject().ToList();
            loopbackOnly = published.Count == 1
                && published[0].Name == RegistryContainerPort
                && published[0].Value.ValueKind == JsonValueKind.Array
                && published[0].Value.GetArrayLength() > 0
                && published[0].Value.EnumerateArray().All(binding =>
                    binding.GetProperty("HostIp").GetString() == LoopbackAddress
                    && binding.GetProperty("HostPort").GetString() == port);
        }

        return (containerImage, running, loopbackOnly);
    }

    private static async Task<string?> GetDigestAsync(
        RenderWorkerImage image, HttpClient http, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Head, new Uri($"v2/{image.BaseRepository}/manifests/{image.BaseTag}", UriKind.Relative));
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return response.Headers.TryGetValues("Docker-Content-Digest", out var values)
            ? values.Single()
            : throw new InvalidOperationException("The local development registry returned no digest.");
    }

    // Runs a command and returns its standard output; standard error is kept apart (logged and reported on failure).
    // When the operation is cancelled, the command and its child processes are stopped.
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
            await process.WaitForExitAsync(cancellationToken);
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
