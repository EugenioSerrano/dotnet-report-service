using System.Globalization;
using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Npgsql;
using ReportService.Migrations;
using ReportService.Testing;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The render worker as the published image: the same process the composition starts, built
/// by the SDK on the PDF base, connected here to a class's own engine, database and storage
/// root. It is the recipe a job-level case uses when it must prove what the deployed worker
/// does - the image carries Chromium and the packaged rendering path - which an in-process
/// host cannot stand in for. A class that needs to replace part of the worker's object graph
/// (a corrupting store, for instance) hosts the worker in its own process instead.
/// </summary>
internal sealed class RenderWorkerContainer : IAsyncDisposable
{
    /// <summary>The image repository the worker project publishes.</summary>
    public const string Repository = "report-service-render-worker";

    /// <summary>The image tag this repository's version produces.</summary>
    public const string Tag = "0.2.0-dev";

    /// <summary>The port the image listens on.</summary>
    private const int ContainerPort = 8080;

    /// <summary>The directory the worker's storage root is mounted at.</summary>
    public const string StoragePath = "/data/storage";

    /// <summary>The directory the worker's template source is mounted at.</summary>
    public const string TemplateSourcePath = "/data/template-source";

    /// <summary>How long the worker may take to answer its own health, Chromium included.</summary>
    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(5);

    private readonly string _id;

    private readonly IContainer? _owned;

    private readonly int _port;

    private readonly HttpClient _health = new();

    private RenderWorkerContainer(IContainer container, int port)
    {
        _owned = container;
        _id = container.Id;
        _port = port;
    }

    /// <summary>
    /// Starts the image against <paramref name="connectionString"/> and <paramref name="enginePort"/>,
    /// with <paramref name="storageRoot"/> and <paramref name="templateSourceRoot"/> mounted as the
    /// worker's own, and waits until its health answers.
    /// </summary>
    /// <param name="connectionString">The connection string the test process uses; the host is rewritten for a container.</param>
    /// <param name="enginePort">The host port the workflow engine is bound to.</param>
    /// <param name="storageRoot">The host directory the composition's storage keeps its objects under.</param>
    /// <param name="templateSourceRoot">The host directory the deployment's template source keeps its packages under.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public static async Task<RenderWorkerContainer> StartAsync(
        string connectionString,
        int enginePort,
        string storageRoot,
        string templateSourceRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateSourceRoot);
        Directory.CreateDirectory(storageRoot);
        Directory.CreateDirectory(templateSourceRoot);

        var database = new NpgsqlConnectionStringBuilder(connectionString) { Host = "host.docker.internal" };
        var builder = new ContainerBuilder($"{Repository}:{Tag}")
            .WithName($"{ContainerSmoke.NamePrefix}render-worker-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..10]}")
            .WithEnvironment("Temporal__Address", $"host.docker.internal:{enginePort.ToString(CultureInfo.InvariantCulture)}")
            .WithEnvironment("Temporal__Namespace", "default")
            .WithEnvironment("Storage__Provider", "filesystem")
            .WithEnvironment("Storage__Filesystem__Root", StoragePath)
            .WithEnvironment("Storage__Filesystem__CreateIfMissing", "false")
            .WithEnvironment("TemplateSource__Store__Filesystem__Root", TemplateSourcePath)
            .WithEnvironment("TemplateSource__Store__Filesystem__CreateIfMissing", "false")
            .WithEnvironment("TemplateSource__ResolveDeadline", "00:04:00")
            .WithEnvironment("ConnectionStrings__reportservice", database.ConnectionString)
            .WithEnvironment("Database__ExpectedSchemaVersion", MigrationCatalog.ExpectedVersion)
            .WithBindMount(storageRoot, StoragePath, AccessMode.ReadWrite)
            .WithBindMount(templateSourceRoot, TemplateSourcePath, AccessMode.ReadOnly)
            .WithPortBinding(ContainerPort, assignRandomHostPort: true);

        var container = builder.Build();
        try
        {
            await container.StartAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await container.DisposeAsync();
            throw new InvalidOperationException(
                $"The render worker image {Repository}:{Tag} did not start. Prepare it once for the run with "
                + "`dotnet run --project src/ReportService.AppHost -c Release -- --prepare-render-worker-image`: "
                + exception.Message,
                exception);
        }

        var worker = new RenderWorkerContainer(container, container.GetMappedPublicPort(ContainerPort));
        try
        {
            await worker.WaitForHealthyAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await worker.DisposeAsync();
            throw;
        }
        catch (Exception exception)
        {
            await worker.DisposeAsync();
            throw new InvalidOperationException(
                $"The render worker image {Repository}:{Tag} started but did not become ready: {exception.Message}",
                exception);
        }

        return worker;
    }

    /// <summary>Waits until the worker answers its own health, the browser probe included.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public Task WaitForHealthyAsync(CancellationToken cancellationToken) =>
        ReportService.Testing.Wait.UntilAsync(
            $"the render worker image answering /health on 127.0.0.1:{_port.ToString(CultureInfo.InvariantCulture)}",
            async token =>
            {
                using var response = await _health.GetAsync(new Uri($"http://127.0.0.1:{_port.ToString(CultureInfo.InvariantCulture)}/health"), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

    /// <summary>The runtime's identifier of this worker's container.</summary>
    public string Id => _id;

    /// <summary>The container's own log, as the runtime keeps it.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public Task<string> LogsAsync(CancellationToken cancellationToken) =>
        DockerAsync(["logs", Id], cancellationToken);

    /// <summary>
    /// Stops the worker the way a lost process stops: <c>docker kill</c> sends SIGKILL, so an
    /// attempt in flight ends with the process and no graceful completion runs.
    /// </summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task StopAsync(CancellationToken cancellationToken) =>
        await DockerAsync(["kill", Id], cancellationToken);

    /// <summary>
    /// Starts the container again and waits until the worker answers its health. With the
    /// composition's <c>--restart=always</c> the runtime may have started it first; a start of
    /// a running container is a no-op and the health wait is what the case observes.
    /// </summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await DockerAsync(["start", Id], cancellationToken);
        await WaitForHealthyAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the container's restart policy from the runtime, as <c>docker inspect</c> reports
    /// it: the runtime's own reading of the argument the composition declared.
    /// </summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <returns>The policy's name, for example <c>always</c>.</returns>
    public async Task<string> RestartPolicyAsync(CancellationToken cancellationToken) =>
        (await DockerAsync(
            ["inspect", "--format", "{{.HostConfig.RestartPolicy.Name}}", Id],
            cancellationToken)).Trim();

    private static async Task<string> DockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker {string.Join(' ', arguments)} failed with exit code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}: {error}");
        }

        return output;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _health.Dispose();
        if (_owned is not null)
        {
            await _owned.DisposeAsync();
        }
    }
}
