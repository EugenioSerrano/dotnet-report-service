extern alias pdf;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Npgsql;
using ReportService.Migrations;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using PdfWorker = pdf::ReportService.Workers.Pdf.PdfWorkerProgram;

namespace ReportService.IntegrationTests.Templates;

/// <summary>
/// The render worker's process over its real image. The refused start: a source class whose one
/// publication record cannot be read stops the process with the decided exit code, after one log
/// line naming the setting and the reason and with no unhandled exception. The normal start: a
/// valid class becomes healthy through the unchanged start check and a graceful stop ends the
/// process with zero. The image is published once for the class, on the pinned PDF base image by
/// digest and with a tag unique to the run, so a stale image can never satisfy a case.
/// </summary>
public sealed class TemplateSourceStartupProcessTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Template = "invoice-energy";
    private const string Version = "1.1.0";
    private const string Consumer = "consumer-a";
    private const int WorkerPort = 8080;

    /// <summary>The project whose image is published: the render worker's container entry.</summary>
    private static readonly string EntryProject =
        Path.Combine("src", "ReportService.Workers.Pdf.Entry", "ReportService.Workers.Pdf.Entry.csproj");

    private static readonly Lazy<Task<string>> Published = new(PublishAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly string[] ManifestMediaTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    private readonly PostgresFixture _fixture;
    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-startup-process-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private EngineStart.Engine? _engine;
    private int _enginePort;
    private string _database = string.Empty;
    private string _connectionString = string.Empty;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public TemplateSourceStartupProcessTests(PostgresFixture fixture) => _fixture = fixture;

    private string SourceRoot => Path.Combine(_directory, "template-source");

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        _database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = _fixture.Application(_database);
        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _engine = await EngineStart.StartAsync(_engineImage, "temporal-startup-process", cancellationToken);
        _enginePort = _engine.HostPort;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.Container.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    [Fact]
    public async Task TheRefusedStartEndsWithItsDecidedExitCode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var image = await Published.Value;
        WriteMismatchedRecord(SourceRoot);
        var name = $"report-service-refused-start-{Suffix()}";

        await using var container = Settings(name, image, "127.0.0.1:1", UnreachableConnectionString())
            .WithBindMount(SourceRoot, "/template-source", AccessMode.ReadOnly)
            .Build();

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(120));
        await container.StartAsync(bound.Token);
        var exit = await container.GetExitCodeAsync(bound.Token);
        var logs = await LogsAsync(container, "render-worker-refused-start.log");

        Assert.Equal(PdfWorker.RefusedStartExitCode, exit);
        Assert.Contains(
            "setting 'TemplateSource:Store:Filesystem:Root': the template source class cannot be read",
            logs,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWorkerStartsHealthyAndStopsWithExitZero()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var image = await Published.Value;
        await PublishValidSourceAsync(SourceRoot);
        var name = $"report-service-normal-start-{Suffix()}";

        await using var container = Settings(name, image, $"host.docker.internal:{_enginePort}", ReachableConnectionString())
            .WithPortBinding(WorkerPort, assignRandomHostPort: true)
            .WithBindMount(SourceRoot, "/template-source", AccessMode.ReadOnly)
            .Build();

        await container.StartAsync(cancellationToken);
        using (var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{container.GetMappedPublicPort(WorkerPort)}") })
        {
            await ReportService.Testing.Wait.UntilAsync(
                "/health answering 200 on the container's published port",
                async token =>
                {
                    using var response = await http.GetAsync(new Uri("/health", UriKind.Relative), token);
                    return response.StatusCode == HttpStatusCode.OK;
                },
                TimeSpan.FromMinutes(3),
                TimeSpan.FromMilliseconds(250),
                cancellationToken);
        }

        // The container's own graceful stop: SIGTERM, the host's shutdown, and the process's
        // decided end. The case stops a healthy process; the in-check cancellation is the
        // hermetic case's, never a race here.
        var stopped = await RunAsync(
            RepositoryPaths.Root, "docker", ["stop", "--time", "30", name], cancellationToken);
        Assert.True(stopped.ExitCode == 0, $"docker stop exited {stopped.ExitCode}: {stopped.Output}");

        var exit = await container.GetExitCodeAsync(cancellationToken);
        var logs = await LogsAsync(container, "render-worker-normal-start.log");

        Assert.Equal(0, exit);
        Assert.Contains("Template source ", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "setting 'TemplateSource:Store:Filesystem:Root'", logs, StringComparison.Ordinal);
    }

    // The common environment of both cases: the source's own root, a writable storage root, the
    // schema version the tree embeds, and the namespace and resolution deadline the worker
    // validates. The refused case points the database at nothing and the engine at a closed port;
    // the normal case gives it the fixture's real dependencies through the host's own address.
    private static ContainerBuilder Settings(string name, string image, string engineAddress, string connectionString) =>
        new ContainerBuilder(image)
            .WithName(name)
            .WithEnvironment("TemplateSource__Store__Filesystem__Root", "/template-source")
            .WithEnvironment("TemplateSource__Store__Filesystem__CreateIfMissing", "false")
            .WithEnvironment("TemplateSource__ResolveDeadline", "00:04:00")
            .WithEnvironment("Storage__Provider", "filesystem")
            .WithEnvironment("Storage__Filesystem__Root", "/tmp/storage")
            .WithEnvironment("Storage__Filesystem__CreateIfMissing", "true")
            .WithEnvironment("ConnectionStrings__reportservice", connectionString)
            .WithEnvironment("Database__ExpectedSchemaVersion", MigrationCatalog.ExpectedVersion)
            .WithEnvironment("Temporal__Address", engineAddress)
            .WithEnvironment("Temporal__Namespace", "default");

    private string ReachableConnectionString() =>
        new NpgsqlConnectionStringBuilder(_connectionString) { Host = "host.docker.internal" }.ConnectionString;

    private static string UnreachableConnectionString() =>
        "Host=127.0.0.1;Port=1;Username=unreachable;Password=unreachable;Database=unreachable;Timeout=1";

    // One publication record whose bytes do not match the SHA-256 its name carries: the check
    // lists it, opens it verified, drains it, and the integrity check refuses the read.
    private void WriteMismatchedRecord(string root)
    {
        var folder = Path.Combine(root, "template-source", Template, "published", Version);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(
            Path.Combine(folder, new string('a', 64) + ".json"),
            Encoding.UTF8.GetBytes(
                $$"""{"descriptorVersion":"1.0","templateId":"{{Template}}","version":"{{Version}}","files":[],"fileCount":0,"totalSize":0}"""));
    }

    private static async Task PublishValidSourceAsync(string root)
    {
        var source = await TemplateSourceClass.CreateAsync(root);
        try
        {
            await source.PublishAsync(Template, Version, TemplatePackageFixtures.Valid(Template, Version, Consumer));
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    private static async Task<string> LogsAsync(IContainer container, string evidenceName)
    {
        var (stdout, stderr) = await container.GetLogsAsync();
        var logs = string.Join('\n', new[] { stdout, stderr }.Where(part => !string.IsNullOrWhiteSpace(part)));
        RunEnvironment.WriteEvidence(evidenceName, Encoding.UTF8.GetBytes(logs));
        return logs;
    }

    private static async Task<string> PublishAsync()
    {
        var project = Path.Combine(RepositoryPaths.Root, EntryProject);
        var properties = await ReadPropertiesAsync(project);
        var digest = await BaseDigestAsync(properties.Registry, properties.Repository, properties.BaseTag);
        var tag = $"it-{RunEnvironment.Stamp()}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        var arguments = new List<string>
        {
            "publish", project, "-c", "Release", "-t:PublishContainer",
            $"-p:PdfBaseImageDigest={digest}", $"-p:ContainerImageTag={tag}",
        };

        var published = await RunAsync(RepositoryPaths.Root, "dotnet", arguments);
        RunEnvironment.WriteEvidence(
            "render-worker-startup-image.log",
            Encoding.UTF8.GetBytes(
                $"> dotnet {string.Join(' ', arguments)}{Environment.NewLine}exit code {published.ExitCode}{Environment.NewLine}{published.Output}"));
        if (published.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The render worker's image could not be published: dotnet {string.Join(' ', arguments)} exited {published.ExitCode}; "
                + "its log is the run's evidence/render-worker-startup-image.log."
                + Environment.NewLine + published.Output);
        }

        return $"{properties.ContainerRepository}:{tag}";
    }

    private static async Task<BaseProperties> ReadPropertiesAsync(string project)
    {
        var read = await RunAsync(
            RepositoryPaths.Root,
            "dotnet",
            [
                "msbuild", project, "-getProperty:ContainerRepository",
                "-getProperty:PdfBaseImageRegistry", "-getProperty:PdfBaseImageRepository", "-getProperty:PdfBaseImageTag",
            ]);
        if (read.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The render worker's entry project could not be read: dotnet msbuild {project} exited {read.ExitCode}."
                + Environment.NewLine + read.Output);
        }

        using var json = JsonDocument.Parse(read.Output);
        var properties = json.RootElement.GetProperty("Properties");
        string Value(string name) => properties.GetProperty(name).GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"The render worker's entry project does not define {name}.");
        return new BaseProperties(
            Value("ContainerRepository"),
            Value("PdfBaseImageRegistry"),
            Value("PdfBaseImageRepository"),
            Value("PdfBaseImageTag"));
    }

    private static async Task<string> BaseDigestAsync(string registry, string repository, string baseTag)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{registry}/") };
        foreach (var mediaType in ManifestMediaTypes)
        {
            http.DefaultRequestHeaders.Accept.ParseAdd(mediaType);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Head, new Uri($"v2/{repository}/manifests/{baseTag}", UriKind.Relative));
        try
        {
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    $"The local development registry holds no {registry}/{repository}:{baseTag}. Prepare the images with: "
                    + "dotnet run --project src/ReportService.AppHost -c Release -- --prepare-render-worker-image");
            }

            response.EnsureSuccessStatusCode();
            return response.Headers.TryGetValues("Docker-Content-Digest", out var values)
                ? values.Single()
                : throw new InvalidOperationException("The local development registry returned no digest for the PDF base image.");
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"The local development registry on {registry} does not answer. Start the composition once "
                + "(dotnet run --project src/ReportService.AppHost) so the registry and the PDF base image exist.",
                exception);
        }
    }

    private static async Task<RunResult> RunAsync(
        string workingDirectory, string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
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

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new RunResult(process.ExitCode, output + error);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];

    private sealed record BaseProperties(string ContainerRepository, string Registry, string Repository, string BaseTag);

    private sealed record RunResult(int ExitCode, string Output);
}
