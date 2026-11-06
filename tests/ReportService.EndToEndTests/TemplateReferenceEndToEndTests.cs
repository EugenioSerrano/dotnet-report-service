using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Dapper;
using Npgsql;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Uploads;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;

namespace ReportService.EndToEndTests;

/// <summary>
/// The template of a report job over the whole composition: the API, the orchestration
/// worker and the PDF worker in their own containers, the real metadata database, and a
/// template source this class serves itself, over TLS, on a port of this machine. The
/// composition reads that source by the name a container uses for this machine and trusts
/// it through the certificate authority the case generated, supplied to the worker as a
/// public certificate file and trusted by that one client, so the version a job resolves is
/// exactly the one the case seeded. A job that names one version is pinned to it after the
/// <c>202</c>; a job that names only its template is pinned to the highest release the
/// source holds; a range is refused at once and creates no job.
/// <para>
/// The pin is read from the composition's database, because the status read does not show
/// it. The class stays small on purpose: every verdict it asserts is the tree's, because
/// the source it reads is one it started and seeded — the requests the worker sent are
/// kept there, which is what shows they arrived and carried no credential, and the commit
/// the pin records is the source's own.
/// </para>
/// </summary>
public sealed class TemplateReferenceEndToEndTests : IDisposable
{
    /// <summary>The template the case's source holds.</summary>
    public const string Template = "invoice-energy";

    /// <summary>The version the case's source holds, and the highest release a by-name job resolves to.</summary>
    public const string Version = "1.1.0";

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

    private TestCertificateAuthority? _authority;
    private X509Certificate2? _leaf;
    private string? _anchor;

    /// <summary>Creates the directory the temporary registry and the trust anchor live in.</summary>
    public TemplateReferenceEndToEndTests() => Directory.CreateDirectory(_directory);

    /// <inheritdoc />
    public void Dispose()
    {
        _leaf?.Dispose();
        _authority?.Dispose();

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
    public async Task TheExactVersionIsPinnedAfterTheAcceptanceAndTheByNameJobTakesTheHighestRelease()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var source = await StartSourceAsync(cancellationToken);
        var (app, api) = await StartAsync(cancellationToken, source);
        await using var composition = app;
        var connectionString = await ConnectionStringAsync(app, cancellationToken);
        var uploadId = await ReadyUploadAsync(api, cancellationToken);

        // An exact reference: the version is taken from the source this case serves and pinned
        // with the values the take read.
        var exact = await AcceptedJobAsync(api, uploadId, Version, cancellationToken);
        var exactPin = await PinnedAsync(connectionString, exact, cancellationToken);
        Assert.Equal(Version, exactPin.ResolvedVersion);
        Assert.Equal(source.Stub.CommitSha, exactPin.CommitSha);
        Assert.Equal(40, exactPin.CommitSha.Length);
        Assert.Equal(64, exactPin.PackageSha256.Length);
        Assert.Equal("SCRIBAN", exactPin.Engine);
        Assert.Equal("schema.json", exactPin.SchemaRef);
        Assert.True(exactPin.ResolvedAt > DateTimeOffset.MinValue);

        // The take reached the source this case serves, and carried no credential: the requests
        // the worker's client made are the ones the source recorded, every one of them addressed
        // to the source's repository, and none of them authorized.
        Assert.NotEmpty(source.Stub.Requests);
        Assert.All(
            source.Stub.Requests,
            request => Assert.StartsWith(
                $"/repos/{GitHubTemplateSourceStub.Owner}/{GitHubTemplateSourceStub.Repository}/",
                request.Path,
                StringComparison.Ordinal));
        Assert.All(source.Stub.Requests, request => Assert.Empty(request.Authorization));

        // By name: the highest release the source holds, never a pre-release, and never the
        // version a previous job happened to name.
        var byName = await AcceptedJobAsync(api, uploadId, version: null, cancellationToken);
        var byNamePin = await PinnedAsync(connectionString, byName, cancellationToken);
        Assert.Equal(Version, byNamePin.ResolvedVersion);
        Assert.Equal(exactPin.PackageSha256, byNamePin.PackageSha256);

        // The version a job uses never changes: the first job's pin is the same after the second.
        var again = await PinnedAsync(connectionString, exact, cancellationToken);
        Assert.Equal(exactPin.ResolvedVersion, again.ResolvedVersion);
        Assert.Equal(exactPin.ResolvedAt, again.ResolvedAt);
    }

    [Fact]
    public async Task ARangeIsRefusedAndNoJobIsCreated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var source = await StartSourceAsync(cancellationToken);
        var (app, api) = await StartAsync(cancellationToken, source);
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

    /// <summary>
    /// Starts the template source this case serves: an authority generated for the case, a
    /// listener certificate naming the address a container on this machine reaches the host
    /// by, the authority's public certificate written as the file the composition supplies as
    /// the source's trust anchor, and the double seeded with the one version the case resolves.
    /// It is started before the composition — the anchor must exist when the composition turns
    /// the path into a mount — and it outlives it, because the worker may read until it stops.
    /// </summary>
    private async Task<TemplateSourceTlsHost> StartSourceAsync(CancellationToken cancellationToken)
    {
        _authority = TestCertificateAuthority.Create("report-service end-to-end template source");
        var leaf = _authority.IssueSourceCertificate(dnsNames: [TestComposition.ContainerHostName]);
        _leaf = leaf;
        _anchor = _authority.WritePem(_directory);

        var stub = new GitHubTemplateSourceStub();
        stub.SeedPackage(Template, Version, TemplatePackageFixtures.Valid(Template, Version, Consumer));
        return await TemplateSourceTlsHost.StartAsync(stub, leaf, cancellationToken);
    }

    private async Task<(DistributedApplication App, HttpClient Api)> StartAsync(
        CancellationToken cancellationToken,
        TemplateSourceTlsHost source)
    {
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(registry, Registry, cancellationToken);
        var anchor = _anchor
            ?? throw new InvalidOperationException("the source's trust anchor has to be written before the composition starts");
        var appHost = await TestComposition.CreateAsync(
            cancellationToken,
            $"--Consumers:File={registry}",
            $"--TemplateSource:ApiBaseUrl={SourceUrl(source)}",
            $"--TemplateSource:Repository={GitHubTemplateSourceStub.Owner}/{GitHubTemplateSourceStub.Repository}",
            $"--TemplateSource:Branch={GitHubTemplateSourceStub.Branch}",
            $"--TemplateSource:TrustAnchor={anchor}");
        var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(HealthyDeadline);
            await app.ResourceNotifications.WaitForResourceHealthyAsync("api", deadline.Token);
        }

        return (app, app.CreateHttpClient("api"));
    }

    /// <summary>
    /// The address the worker reads the source at: the port the case's own listener reported,
    /// reached by the name a container on this machine dials the host by. The name is the one
    /// a container resolves to this machine, never this machine's loopback, which a container
    /// cannot reach.
    /// </summary>
    private static string SourceUrl(TemplateSourceTlsHost source) => string.Create(
        CultureInfo.InvariantCulture,
        $"https://{TestComposition.ContainerHostName}:{source.BaseUrl.Port}/");

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

    private static async Task<string> AcceptedJobAsync(
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

    private static Task<HttpResponseMessage> SendJobAsync(
        HttpClient api,
        string uploadId,
        string? version,
        CancellationToken cancellationToken)
    {
        var template = version is null
            ? $$"""{"id":"{{Template}}"}"""
            : $$"""{"id":"{{Template}}","version":"{{version}}"}""";
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

    private static string Consumer => "consumer-a";

    /// <summary>One reference row, as the composition's database holds it.</summary>
    private sealed class PinRow
    {
        public string TemplateId { get; set; } = string.Empty;

        public string ReferenceForm { get; set; } = string.Empty;

        public string? RequestedVersion { get; set; }

        public string? BaselineVersion { get; set; }

        public string ResolvedVersion { get; set; } = string.Empty;

        public string CommitSha { get; set; } = string.Empty;

        public string PackageSha256 { get; set; } = string.Empty;

        public string Engine { get; set; } = string.Empty;

        public string EngineVersion { get; set; } = string.Empty;

        public string SchemaRef { get; set; } = string.Empty;

        public DateTimeOffset ResolvedAt { get; set; }
    }
}
