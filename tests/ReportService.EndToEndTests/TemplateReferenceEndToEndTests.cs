using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Uploads;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource.Source;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;

namespace ReportService.EndToEndTests;

/// <summary>
/// The template of a report job over the whole composition: the API, the orchestration worker
/// and the PDF worker in their own containers, the real metadata database, and a version
/// published in the composition's own object store. A job that names one version is pinned to
/// it after the <c>202</c>; a job that names only its template is pinned to the highest release
/// the store holds; a range is refused at once and creates no job.
/// <para>
/// The pin is read from the composition's database, because the status read does not show it.
/// The versions the class reads are published into the root the three processes share before
/// the composition starts, under a template identifier of this run's own, so nothing a previous
/// run left behind decides what this one asserts.
/// </para>
/// </summary>
public sealed class TemplateReferenceEndToEndTests : IDisposable
{
    /// <summary>The consumer the composition accepts jobs for.</summary>
    private const string Consumer = "consumer-a";

    /// <summary>The version the exact job names, and the highest release a by-name job resolves to.</summary>
    private const string Version = "1.1.0";

    /// <summary>The release published beside <see cref="Version"/>, which a by-name job must not pick.</summary>
    private const string EarlierVersion = "1.0.0";

    /// <summary>The setting the render worker keeps: the bound of one by-name resolution.</summary>
    private const string ResolveDeadlineKey = "TemplateSource__ResolveDeadline";

    /// <summary>The render worker's settings of the source's own root.</summary>
    private const string SourceRootKey = "TemplateSource__Store__Filesystem__Root";

    private const string SourceCreateIfMissingKey = "TemplateSource__Store__Filesystem__CreateIfMissing";

    /// <summary>The source's root as the render worker's container is given it.</summary>
    private const string SourceContainerPath = "/data/template-source";

    /// <summary>The writable root as the render worker's container is given it.</summary>
    private const string StorageContainerPath = "/data/storage";

    /// <summary>The source class's directory name under the repository root.</summary>
    private const string SourceDirectoryName = ".template-source";

    /// <summary>The template-source settings the product no longer reads.</summary>
    private static readonly string[] RemovedKeys =
    [
        "TemplateSource__Repository",
        "TemplateSource__Branch",
        "TemplateSource__ApiBaseUrl",
        "TemplateSource__AllowInsecureLoopback",
        "TemplateSource__Token",
    ];

    private const string Registry = """
        { "consumers": [ { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true } ] }
        """;

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan PinDeadline = TimeSpan.FromMinutes(3);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e-template",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private readonly string _template = "invoice-energy-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public TemplateReferenceEndToEndTests() => Directory.CreateDirectory(_directory);

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task TheExactVersionIsPinnedAfterTheAcceptanceAndTheByNameJobTakesTheHighestRelease()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (app, api) = await StartAsync(cancellationToken);
        await using var composition = app;
        var connectionString = await ConnectionStringAsync(app, cancellationToken);
        var uploadId = await ReadyUploadAsync(api, cancellationToken);

        // An exact reference: the version is taken from the composition's own store and pinned
        // with the values the take read.
        var exact = await AcceptedJobAsync(api, uploadId, Version, cancellationToken);
        var exactPin = await PinnedAsync(connectionString, exact, cancellationToken);
        Assert.Equal(Version, exactPin.ResolvedVersion);
        Assert.Null(exactPin.CommitSha);
        Assert.Equal(64, exactPin.SourceDescriptorSha256!.Length);
        Assert.Equal(64, exactPin.PackageSha256.Length);
        Assert.Equal("SCRIBAN", exactPin.Engine);
        Assert.Equal("schema.json", exactPin.SchemaRef);
        Assert.True(exactPin.ResolvedAt > DateTimeOffset.MinValue);
        Assert.Matches("^[0-9a-f]{64}$", exactPin.SourceDescriptorSha256);
        AssertThePublishedFiles(exactPin);
        Assert.Equal(_template, exactPin.TemplateId);

        // By name: the highest release the store holds, never a pre-release, and never the
        // version a previous job happened to name.
        var byName = await AcceptedJobAsync(api, uploadId, version: null, cancellationToken);
        var byNamePin = await PinnedAsync(connectionString, byName, cancellationToken);
        Assert.Equal(Version, byNamePin.ResolvedVersion);
        Assert.Equal(exactPin.PackageSha256, byNamePin.PackageSha256);
        Assert.Equal(exactPin.SourceDescriptorSha256, byNamePin.SourceDescriptorSha256);

        // The version a job uses never changes: the first job's pin is the same after the second.
        var again = await PinnedAsync(connectionString, exact, cancellationToken);
        Assert.Equal(exactPin.ResolvedVersion, again.ResolvedVersion);
        Assert.Equal(exactPin.ResolvedAt, again.ResolvedAt);

        // And the class the take read is the source's: the writable root holds none of it.
        AssertTheWritableRootHoldsNoSourceClassOfThisRun();
    }

    [Fact]
    public async Task ARangeIsRefusedAndNoJobIsCreated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // The composition is started with the template-source settings the product no longer
        // reads, and a planted credential-shaped value among them: they are inert, and nothing
        // of them reaches the worker.
        var (app, api) = await StartAsync(
            cancellationToken,
            "--TemplateSource:Repository=report-service/templates",
            "--TemplateSource:Branch=main",
            "--TemplateSource:ApiBaseUrl=http://127.0.0.1:9/",
            "--TemplateSource:AllowInsecureLoopback=true",
            "--TemplateSource:Token=ghp_plantedTokenValueNeverLoaded1234567890");
        await using var composition = app;
        var connectionString = await ConnectionStringAsync(app, cancellationToken);
        var uploadId = await ReadyUploadAsync(api, cancellationToken);
        var before = await JobCountAsync(connectionString, cancellationToken);

        using (var refused = await SendJobAsync(api, uploadId, "^1.0.0", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            using var document = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(ErrorCodes.TemplateNotFound, document.RootElement.GetProperty("code").GetString());
        }

        // Nothing was accepted: no job row and no reference, so nothing was taken either.
        Assert.Equal(before, await JobCountAsync(connectionString, cancellationToken));
    }

    private static async Task AssertTheCompositionCarriesNoTemplateSourceSettingAsync(
        DistributedApplication app,
        string scenario,
        CancellationToken cancellationToken)
    {
        var environment = await EffectiveEnvironmentAsync(app, TestComposition.RenderWorkerName, cancellationToken);
        Assert.True(
            environment.ContainsKey(ResolveDeadlineKey),
            $"{scenario}: the render worker is not given {ResolveDeadlineKey}, the one template-source setting a by-name resolution reads.");
        Assert.True(
            environment.TryGetValue(SourceRootKey, out var root) && Equals(root, SourceContainerPath),
            $"{scenario}: the render worker is not given its own template source root at {SourceContainerPath}.");
        Assert.True(
            environment.TryGetValue(SourceCreateIfMissingKey, out var create) && Equals(create, "false"),
            $"{scenario}: the render worker may create its template source root, and the source reads a root a publisher owns.");
        foreach (var key in RemovedKeys)
        {
            Assert.False(
                environment.ContainsKey(key),
                $"{scenario}: the render worker is given '{key}', a template-source setting the product no longer reads.");
        }

        // The two processes that compose no source reader are given none of its settings.
        foreach (var name in new[] { "api", "orchestration-worker" })
        {
            var other = await EffectiveEnvironmentAsync(app, name, cancellationToken);
            foreach (var key in new[] { SourceRootKey, SourceCreateIfMissingKey, ResolveDeadlineKey })
            {
                Assert.False(
                    other.ContainsKey(key),
                    $"{scenario}: the {name} is given '{key}', a template-source setting of a process that reads none.");
            }
        }
    }

    // The built model's two mounts: the writable root read-write, the source's root read-only.
    private static void AssertTheModelCarriesTheReadOnlyMount(DistributedApplication app, string scenario)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var worker = model.Resources.Single(candidate => string.Equals(candidate.Name, TestComposition.RenderWorkerName, StringComparison.Ordinal));
        var mounts = worker.Annotations.OfType<ContainerMountAnnotation>().ToList();
        var storage = Assert.Single(mounts, mount => string.Equals(mount.Target, StorageContainerPath, StringComparison.Ordinal));
        var source = Assert.Single(mounts, mount => string.Equals(mount.Target, SourceContainerPath, StringComparison.Ordinal));
        Assert.False(storage.IsReadOnly, $"{scenario}: the writable root must stay read-write.");
        Assert.True(source.IsReadOnly, $"{scenario}: the source root must be mounted read-only, and the grant is expressed by that mount.");
        Assert.NotNull(source.Source);
        Assert.NotNull(storage.Source);
        Assert.EndsWith(SourceDirectoryName, source.Source.TrimEnd('/', '\\'), StringComparison.Ordinal);
        Assert.False(
            string.Equals(source.Source, storage.Source, StringComparison.Ordinal),
            $"{scenario}: the source root and the writable root are the same directory, so the separation is not expressed.");
    }

    private static async Task<IReadOnlyDictionary<string, object>> EffectiveEnvironmentAsync(
        DistributedApplication app,
        string resourceName,
        CancellationToken cancellationToken)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = model.Resources.Single(candidate => string.Equals(candidate.Name, resourceName, StringComparison.Ordinal));
        var execution = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var environment = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(new EnvironmentCallbackContext(execution, resource, environment, cancellationToken));
        }

        return environment;
    }

    private async Task<(DistributedApplication App, HttpClient Api)> StartAsync(
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        await PublishAsync(cancellationToken);
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(registry, Registry, cancellationToken);
        var appHost = await TestComposition.CreateAsync(
            cancellationToken,
            [$"--Consumers:File={registry}", .. arguments]);
        var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(HealthyDeadline);
            await app.ResourceNotifications.WaitForResourceHealthyAsync("api", deadline.Token);
        }

        await AssertTheCompositionCarriesNoTemplateSourceSettingAsync(app, "the composition under test", cancellationToken);
        AssertTheModelCarriesTheReadOnlyMount(app, "the composition under test");
        return (app, app.CreateHttpClient("api"));
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        // The version is published only where the composition reads it: the source's own root.
        // Finding it there is itself the observable of the separation.
        var root = Path.Combine(RepositoryPaths.Root, SourceDirectoryName);
        Directory.CreateDirectory(root);
        var source = await TemplateSourceClass.CreateAsync(root);
        try
        {
            await source.PublishAsync(_template, EarlierVersion, TemplatePackageFixtures.Valid(_template, EarlierVersion, Consumer));
            await source.PublishAsync(_template, Version, TemplatePackageFixtures.Valid(_template, Version, Consumer));
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    // The writable root holds nothing of this run's template: the take read it from the source's root.
    private void AssertTheWritableRootHoldsNoSourceClassOfThisRun()
    {
        var written = Path.Combine(
            RepositoryPaths.Root,
            ".storage",
            TemplateSourceKeys.ClassPrefix,
            KeySegmentEncoding.Encode(_template));
        Assert.False(
            Directory.Exists(written),
            $"the writable root holds '{written}', so the take did not read the source's own root");
    }

    private void AssertThePublishedFiles(PinRow pin)
    {
        var expected = PublishedFiles();
        Assert.NotNull(pin.SourceObjects);
        Assert.Equal(expected.Count, pin.SourceObjects!.Length);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Path, pin.SourceObjects[index].Path);
            Assert.Equal(expected[index].Size, pin.SourceObjects[index].Size);
            Assert.Equal(expected[index].Sha256, pin.SourceObjects[index].Sha256);
        }
    }

    // The files the case publishes for the version it reads, ordered by their package path — the
    // order the take records them in — with the size and SHA-256 the publication record declares.
    private List<(string Path, long Size, string Sha256)> PublishedFiles() =>
        [.. TemplatePackageFixtures.Valid(_template, Version, Consumer)
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file => (file.Key, (long)file.Value.Length, Convert.ToHexStringLower(SHA256.HashData(file.Value))))];

    private static async Task<string> ConnectionStringAsync(DistributedApplication app, CancellationToken cancellationToken) =>
        await app.GetConnectionStringAsync("reportservice", cancellationToken)
        ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");

    private static async Task<string> ReadyUploadAsync(HttpClient api, CancellationToken cancellationToken)
    {
        var uploadId = await CreateUploadAsync(api, cancellationToken);
        var body = Encoding.UTF8.GetBytes("""{"header":{},"items":[{"id":1,"v":17}]}""");
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(CreateUploadRequestReader.JsonMediaType);
        using (var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/content", UriKind.Relative))
        {
            Content = content,
        })
        {
            request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
            using var sent = await api.SendAsync(request, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        using (var confirmed = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/complete", UriKind.Relative))
            {
                Content = new StringContent("{}", Encoding.UTF8, CreateUploadRequestReader.JsonMediaType),
                Headers = { { ConsumerIdentity.HeaderName, Consumer } },
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        }

        await Wait.UntilAsync(
            $"the upload {uploadId} to end verified",
            async token =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}", UriKind.Relative));
                request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
                using var response = await api.SendAsync(request, token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return false;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                return document.RootElement.GetProperty("status").GetString() == "ready";
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return uploadId;
    }

    private static async Task<string> CreateUploadAsync(HttpClient api, CancellationToken cancellationToken)
    {
        using var created = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
            {
                Content = new StringContent("""{"profile":"header-items-v1","mode":"API_STREAM"}""", Encoding.UTF8, "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, Consumer } },
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("uploadId").GetString()!;
    }

    private async Task<string> AcceptedJobAsync(
        HttpClient api,
        string uploadId,
        string? version,
        CancellationToken cancellationToken)
    {
        using var response = await SendJobAsync(api, uploadId, version, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"the job had to be accepted: {(int)response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

    private Task<HttpResponseMessage> SendJobAsync(
        HttpClient api,
        string uploadId,
        string? version,
        CancellationToken cancellationToken)
    {
        var template = version is null
            ? $$"""{"id":"{{_template}}"}"""
            : $$"""{"id":"{{_template}}","version":"{{version}}"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                $$"""{"template":{{template}},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":["pdf"]}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        return api.SendAsync(request, cancellationToken);
    }

    private static async Task<PinRow> PinnedAsync(string connectionString, string reportJobId, CancellationToken cancellationToken)
    {
        PinRow? row = null;
        await Wait.UntilAsync(
            $"the template pin of {reportJobId}",
            async token =>
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(token);
                row = await connection.QuerySingleOrDefaultAsync<PinRow>(new CommandDefinition(
                    """
                    SELECT template_id AS "TemplateId", reference_form AS "ReferenceForm",
                           requested_version AS "RequestedVersion", baseline_version AS "BaselineVersion",
                           resolved_version AS "ResolvedVersion", commit_sha AS "CommitSha",
                           source_descriptor_sha256 AS "SourceDescriptorSha256",
                           source_objects::text AS "SourceObjectsJson",
                           package_sha256 AS "PackageSha256", engine AS "Engine",
                           engine_version AS "EngineVersion", schema_ref AS "SchemaRef",
                           resolved_at AS "ResolvedAt"
                      FROM report_service.template_references
                     WHERE report_job_id = @ReportJobId AND resolved_at IS NOT NULL
                    """,
                    new { ReportJobId = reportJobId },
                    cancellationToken: token));
                return row is not null;
            },
            PinDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return row!;
    }

    private static async Task<long> JobCountAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM report_service.report_jobs",
            cancellationToken: cancellationToken));
    }

    /// <summary>One reference row, as the composition's database holds it.</summary>
    private sealed class PinRow
    {
        public string TemplateId { get; set; } = string.Empty;

        public string ReferenceForm { get; set; } = string.Empty;

        public string? RequestedVersion { get; set; }

        public string? BaselineVersion { get; set; }

        public string ResolvedVersion { get; set; } = string.Empty;

        public string? CommitSha { get; set; }

        public string? SourceDescriptorSha256 { get; set; }

        public string? SourceObjectsJson { get; set; }

        public TemplateSourceObject[]? SourceObjects =>
            SourceObjectsJson is null ? null : TemplateSourceObjects.Read(SourceObjectsJson);

        public string PackageSha256 { get; set; } = string.Empty;

        public string Engine { get; set; } = string.Empty;

        public string EngineVersion { get; set; } = string.Empty;

        public string SchemaRef { get; set; } = string.Empty;

        public DateTimeOffset ResolvedAt { get; set; }
    }
}
