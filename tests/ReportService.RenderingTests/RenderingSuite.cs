using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using ReportService.Testing;
using ReportService.Testing.Fixtures;

namespace ReportService.RenderingTests;

/// <summary>
/// One rendering run: the two images, the fixture library, the packages and the
/// folder the documents are printed into. The images are built once per run, the
/// fixtures are written once, and every case asks for a render and a verdict of
/// its own.
/// </summary>
public sealed class RenderingSuite : IAsyncLifetime
{
    /// <summary>The rendering host image the level publishes.</summary>
    public const string HostImage = "report-service-rendering-host:0.2.0-dev";

    /// <summary>The judge's image the level builds.</summary>
    public const string OracleImage = "report-service-pdf-oracle:0.2.0-dev";

    private const string BaseRegistry = "localhost:5055";
    private const string BaseRepository = "report-service-pdf-base";
    private const string BaseTag = "1.63.0-10.0.13-noble";
    private const string HostProject = "tests/ReportService.RenderingHost/ReportService.RenderingHost.csproj";

    private static readonly string[] ManifestMediaTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    private readonly string root = Path.Combine(Path.GetTempPath(), "report-service-rendering-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);
    private int renders;

    /// <summary>The repository root.</summary>
    public static string RepositoryRoot => RepositoryPaths.Root;

    /// <summary>Where the generated snapshots are written.</summary>
    public string FixturesDirectory => Path.Combine(root, "fixtures");

    /// <summary>Where the printed documents, their readings and their verdicts are written.</summary>
    public string OutputDirectory => Path.Combine(root, "out");

    /// <summary>The two template packages the suite prints.</summary>
    public static string TemplatesDirectory => Path.Combine(RepositoryRoot, "tests", "ReportService.Testing", "Templates");

    /// <summary>The sample packages the repository ships, as the folders a consumer copies.</summary>
    public static string SamplesDirectory => Path.Combine(RepositoryRoot, "samples", "templates");

    /// <summary>The declarations of the two packages.</summary>
    public static string ExpectationsDirectory => Path.Combine(RepositoryRoot, "tests", "ReportService.Testing", "OracleExpectations");

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(OutputDirectory);
        WriteFixtures();
        await BuildImagesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // The evidence stays in the temporary folder; a run never fails for it.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Renders <paramref name="fixtureId"/> with <paramref name="package"/> and
    /// returns the result the host wrote; <paramref name="mutation"/> applies a
    /// known-bad control on the way.
    /// </summary>
    public async Task<RenderOutcome> RenderAsync(string fixtureId, string package, string? mutation = null, string? name = null)
    {
        var stem = name ?? (mutation is null ? $"{fixtureId}-{package}" : $"{fixtureId}-{package}-{mutation}");
        var command = new List<string>
        {
            "render", "--fixture", $"/fixtures/{fixtureId}.json", "--template", $"/packages/{package}", "--out", "/out",
        };
        if (mutation is not null)
        {
            command.AddRange(["--mutation", mutation]);
        }

        var exit = await RunContainerAsync(HostImage, command, new[] { (FixturesDirectory, "/fixtures"), (TemplatesDirectory, "/packages"), (OutputDirectory, "/out") }).ConfigureAwait(false);
        var resultPath = Path.Combine(OutputDirectory, (mutation is null ? fixtureId : $"{fixtureId}-{mutation}") + ".json");
        if (exit != 0)
        {
            throw new InvalidOperationException($"The rendering host exited {exit} for {stem}; the manifest of the run is in {resultPath}.");
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath).ConfigureAwait(false));
        var pdf = Path.Combine(OutputDirectory, document.RootElement.GetProperty("pdf").GetString()!);
        var verdict = document.RootElement.GetProperty("verificationPassed").GetBoolean();
        var reason = document.RootElement.TryGetProperty("verificationReason", out var reported) && reported.ValueKind == JsonValueKind.String
            ? reported.GetString()
            : null;
        return new RenderOutcome(fixtureId, package, mutation, pdf, verdict, reason);
    }

    /// <summary>
    /// Prints the shipped sample <paramref name="sampleId"/> from its own folder with
    /// its own snapshot: the package and the payload a consumer copies, both mounted
    /// read-only, printed into the suite's output folder. Nothing is copied,
    /// regenerated or rewritten on the way.
    /// </summary>
    /// <param name="sampleId">The sample's folder name, which is its package id.</param>
    public async Task<ShippedRenderOutcome> RenderShippedSampleAsync(string sampleId)
    {
        var package = Path.Combine(SamplesDirectory, sampleId);
        var command = new List<string>
        {
            "render", "--fixture", "/fixtures/sample.json", "--template", $"/packages/{sampleId}", "--out", "/out",
        };
        (string Source, string Target, AccessMode Mode)[] mounts =
        [
            (SamplesDirectory, "/packages", AccessMode.ReadOnly),
            (package, "/fixtures", AccessMode.ReadOnly),
            (OutputDirectory, "/out", AccessMode.ReadWrite),
        ];

        var exit = await RunContainerAsync(HostImage, command, mounts).ConfigureAwait(false);
        var resultPath = Path.Combine(OutputDirectory, "sample.json");
        if (exit != 0)
        {
            throw new InvalidOperationException($"The rendering host exited {exit} for the shipped sample {sampleId}; its result would be in {resultPath}.");
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath).ConfigureAwait(false));
        var pdf = Path.Combine(OutputDirectory, document.RootElement.GetProperty("pdf").GetString()!);
        var verdict = document.RootElement.GetProperty("verificationPassed").GetBoolean();
        var reason = document.RootElement.TryGetProperty("verificationReason", out var reported) && reported.ValueKind == JsonValueKind.String
            ? reported.GetString()
            : null;
        var aborted = document.RootElement.GetProperty("abortedRequests").GetInt32();
        return new ShippedRenderOutcome(pdf, verdict, reason, aborted);
    }

    /// <summary>Reads <paramref name="pdf"/> with the judge and returns its verdict.</summary>
    public async Task<Verdict> JudgeAsync(RenderOutcome render)
    {
        var stem = Path.GetFileNameWithoutExtension(render.Pdf);
        var verdictPath = Path.Combine(OutputDirectory, stem + ".verdict.json");
        var readingPath = Path.Combine(OutputDirectory, stem + ".reading.json");
        var exit = await RunContainerAsync(
            OracleImage,
            [
                "verdict", "--pdf", $"/out/{Path.GetFileName(render.Pdf)}",
                "--fixture", $"/fixtures/{render.Fixture}.json",
                "--expectation", $"/expectations/{render.Package}.json",
                "--out", $"/out/{Path.GetFileName(verdictPath)}",
                "--reading", $"/out/{Path.GetFileName(readingPath)}",
            ],
            [(FixturesDirectory, "/fixtures"), (ExpectationsDirectory, "/expectations"), (OutputDirectory, "/out")]).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidOperationException($"The judge exited {exit} for {stem}; it produced no verdict.");
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(verdictPath).ConfigureAwait(false));
        var failures = document.RootElement.GetProperty("failures").EnumerateArray()
            .Select(failure => Describe(failure))
            .ToList();
        return new Verdict(
            Passed: document.RootElement.GetProperty("passed").GetBoolean(),
            Pages: document.RootElement.GetProperty("pages").GetInt32(),
            Words: document.RootElement.GetProperty("words").GetInt32(),
            Classes: failures,
            Reading: await File.ReadAllTextAsync(readingPath).ConfigureAwait(false));
    }

    private static string Describe(JsonElement failure)
    {
        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        return string.Join(
            " | ",
            new[]
            {
                Text(failure, "rule"),
                Text(failure, "class"),
                Text(failure, "field"),
                Text(failure, "detail"),
            }.Where(part => !string.IsNullOrEmpty(part)));
    }

    /// <summary>The words of a reading file, in the order the judge read them.</summary>
    public static IReadOnlyList<string> ReadingWords(string reading)
    {
        using var document = JsonDocument.Parse(reading);
        return [.. document.RootElement.GetProperty("words").EnumerateArray().Select(word => word.GetProperty("text").GetString()!)];
    }

    private void WriteFixtures()
    {
        var datasets = Path.Combine(RepositoryRoot, "samples", "datasets");
        var dataset = Path.Combine(datasets, "header-items-v1.grouped.10k.json");
        if (!File.Exists(dataset))
        {
            throw new InvalidOperationException(
                $"The pilot dataset {dataset} is missing. Generate it with: dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- generate");
        }

        var generator = new FixtureGenerator(FixtureDataset.Load(dataset, Path.Combine(datasets, FixtureGenerator.SumsFileName)));
        generator.WriteFolder(FixturesDirectory);
    }

    private async Task BuildImagesAsync()
    {
        var digest = await BaseDigestAsync().ConfigureAwait(false);
        await RunAsync(
            RepositoryRoot,
            "dotnet",
            ["publish", HostProject, "-c", "Release", "-t:PublishContainer", $"-p:PdfBaseImageDigest={digest}"]).ConfigureAwait(false);
        await RunAsync(
            RepositoryRoot,
            "docker",
            ["build", "-f", "eng/pdf-oracle/Dockerfile", "-t", OracleImage, "."]).ConfigureAwait(false);
    }

    private static async Task<string> BaseDigestAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{BaseRegistry}/") };
        foreach (var mediaType in ManifestMediaTypes)
        {
            http.DefaultRequestHeaders.Accept.ParseAdd(mediaType);
        }

        using var request = new HttpRequestMessage(HttpMethod.Head, new Uri($"v2/{BaseRepository}/manifests/{BaseTag}", UriKind.Relative));
        try
        {
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    $"The local development registry holds no {BaseRegistry}/{BaseRepository}:{BaseTag}. Prepare the images with: dotnet run --project src/ReportService.AppHost -c Release -- --prepare-render-worker-image");
            }

            response.EnsureSuccessStatusCode();
            return response.Headers.TryGetValues("Docker-Content-Digest", out var values)
                ? values.Single()
                : throw new InvalidOperationException("The local development registry returned no digest for the PDF base image.");
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"The local development registry on {BaseRegistry} does not answer. Start the composition once (dotnet run --project src/ReportService.AppHost) so the registry and the PDF base image exist.",
                exception);
        }
    }

    private Task<long> RunContainerAsync(string image, IReadOnlyList<string> command, IReadOnlyList<(string Source, string Target)> mounts) =>
        RunContainerAsync(
            image,
            command,
            mounts.Select(mount => (mount.Source, mount.Target, AccessMode.ReadWrite)).ToArray());

    private async Task<long> RunContainerAsync(
        string image,
        IReadOnlyList<string> command,
        IReadOnlyList<(string Source, string Target, AccessMode Mode)> mounts)
    {
        var unique = Interlocked.Increment(ref renders).ToString("D3", CultureInfo.InvariantCulture);
        var builder = new ContainerBuilder(image)
            .WithName($"report-service-rendering-{unique}-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]}")
            .WithCommand([.. command]);
        foreach (var (source, target, mode) in mounts)
        {
            builder = builder.WithBindMount(source, target, mode);
        }

        await using var container = builder.Build();
        await container.StartAsync().ConfigureAwait(false);
        return await container.GetExitCodeAsync().ConfigureAwait(false);
    }

    private static async Task RunAsync(string workingDirectory, string fileName, IReadOnlyList<string> arguments)
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
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {string.Join(' ', arguments)} exited {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
    }
}

/// <summary>One printed document: what it was rendered from and where it is.</summary>
/// <param name="Fixture">The fixture the block was read from.</param>
/// <param name="Package">The package it was composed with.</param>
/// <param name="Mutation">The control applied, or null for none.</param>
/// <param name="Pdf">The PDF the print wrote.</param>
public sealed record RenderOutcome(
    string Fixture,
    string Package,
    string? Mutation,
    string Pdf,
    bool VerificationPassed,
    string? VerificationReason);

/// <summary>One printed shipped sample: where the document is and what the product's verification said about it.</summary>
/// <param name="Pdf">The PDF the print wrote.</param>
/// <param name="VerificationPassed">Whether the product's verification accepted it.</param>
/// <param name="VerificationReason">The reason its verification refused it with, or null.</param>
/// <param name="AbortedRequests">How many requests of the document the route policy aborted.</param>
public sealed record ShippedRenderOutcome(
    string Pdf,
    bool VerificationPassed,
    string? VerificationReason,
    int AbortedRequests);

/// <summary>What the judge said about one printed document.</summary>
/// <param name="Passed">Whether every rule passed.</param>
/// <param name="Pages">How many pages it read.</param>
/// <param name="Words">How many words it read.</param>
/// <param name="Classes">The failure classes, in the order the judge reported them.</param>
/// <param name="Reading">The reading the verdict was reached from.</param>
public sealed record Verdict(bool Passed, int Pages, int Words, IReadOnlyList<string> Classes, string Reading);
