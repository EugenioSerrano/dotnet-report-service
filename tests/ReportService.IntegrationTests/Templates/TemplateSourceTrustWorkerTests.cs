extern alias pdf;

using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Npgsql;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Take;
using pdf::ReportService.Workers.Pdf.Templates;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Worker;
using PdfWorker = pdf::ReportService.Workers.Pdf.PdfWorkerProgram;

namespace ReportService.IntegrationTests.Templates;

/// <summary>
/// The source client's trust on the product's own render worker: a real engine, a
/// real metadata database, a real storage root, and the source served over real
/// TLS by this class's own listener. With the anchor that issued the source's
/// certificate the take completes; with an anchor naming another authority the
/// take ends <c>TEMPLATE_SOURCE_UNAVAILABLE</c>; with no anchor at all it ends the
/// same way, because the platform store does not trust that certificate. Each case
/// starts its own worker host, so the anchor is read once per host and never shared
/// between them, and nothing this class does is visible to another client: a plain
/// <see cref="HttpClient"/> refuses the same endpoint, and neither generated
/// authority reaches a trust store.
/// </summary>
public sealed class TemplateSourceTrustWorkerTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Owner = "billing-system";
    private const string Template = "invoice-energy";
    private const string Version = "1.1.0";
    private const string TestQueue = "template-source-trust-tests";

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(4);

    private readonly PostgresFixture _fixture;
    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-trust-worker-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private EngineStart.Engine? _engine;
    private string _connectionString = string.Empty;
    private NpgsqlDataSource? _dataSource;
    private TemplateSourceTlsHost? _source;
    private X509Certificate2? _serverCertificate;
    private TestCertificateAuthority? _authority;
    private TestCertificateAuthority? _otherAuthority;
    private WebApplicationFactory<PdfWorker>? _worker;
    private TemporalWorker? _testWorker;
    private readonly CancellationTokenSource _testWorkerStop = new();
    private Task? _testWorkerRun;
    private TemporalClient? _client;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public TemplateSourceTrustWorkerTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        try
        {
            await OpenAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task OpenAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);

        var database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = _fixture.Application(database);
        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(_connectionString);

        _engine = await EngineStart.StartAsync(_engineImage, "temporal-template-trust", cancellationToken);

        var stub = new GitHubTemplateSourceStub();
        stub.SeedPackage(Template, Version, TemplatePackageFixtures.Valid(Template, Version, Owner));

        _authority = TestCertificateAuthority.Create("report-service test source");
        _otherAuthority = TestCertificateAuthority.Create("report-service other authority");
        _serverCertificate = _authority.IssueSourceCertificate();
        _source = await TemplateSourceTlsHost.StartAsync(stub, _serverCertificate, cancellationToken);

        _client = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions($"127.0.0.1:{_engine.HostPort}") { Namespace = "default" }).WaitAsync(cancellationToken);

        _testWorker = new TemporalWorker(
            _client,
            new TemporalWorkerOptions(TestQueue).AddWorkflow<TakeVersionForTestWorkflow>());
        _testWorkerRun = _testWorker.ExecuteAsync(_testWorkerStop.Token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _testWorkerStop.CancelAsync();
        if (_testWorkerRun is not null)
        {
            try
            {
                await _testWorkerRun.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The test worker is stopped: that is how it ends.
            }
        }

        _testWorker?.Dispose();
        _testWorkerStop.Dispose();
        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_engine is not null)
        {
            await _engine.Container.DisposeAsync();
        }

        if (_source is not null)
        {
            await _source.DisposeAsync();
        }

        _serverCertificate?.Dispose();
        _authority?.Dispose();
        _otherAuthority?.Dispose();

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
    public async Task TheAnchorThatIssuedTheSourcesCertificateLetsTheTakeReadItAndNothingElseLearnsTheTrust()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var logs = new LogCapture();
        var anchorPath = _authority!.WritePem(Path.Combine(_directory, "anchor"));
        _worker = await StartWorkerAsync(anchorPath, logs, cancellationToken);

        // The handler the client is built with, on its own: the source's certificate chains
        // to the anchor, so the endpoint answers instead of failing the handshake.
        using (var anchoredHandler = TemplateSourceHttpHandler.Create([_authority.Authority]))
        using (var anchored = new HttpClient(anchoredHandler))
        using (var answered = await anchored.GetAsync(_source!.BaseUrl, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, answered.StatusCode);
        }

        var result = await TakeAsync(Version, cancellationToken);

        Assert.Equal(TakeTemplateVersionOutcomes.Taken, result.Outcome);
        var row = await RowAsync(Version, cancellationToken);
        Assert.Equal(Owner, row.OwnerConsumerId);
        Assert.Equal(result.CommitSha, row.CommitSha);
        Assert.Equal(result.PackageSha256, row.PackageSha256);
        Assert.NotEmpty(_source!.Stub.Requests);

        // The one start-up line names the setting and the count, never the path.
        Assert.Contains(logs.Lines, line => line.Contains(
            "the template source trusts 1 supplied certificate(s) from TrustAnchor", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains(anchorPath, StringComparison.Ordinal));

        // The trust is the one client's: a plain client in this process still refuses the source...
        using var plain = new HttpClient();
        var refused = await Assert.ThrowsAsync<HttpRequestException>(
            () => plain.GetAsync(_source.BaseUrl, cancellationToken));
        Assert.Contains(
            Chain(refused),
            exception => exception is System.Security.Authentication.AuthenticationException);

        // ...and neither generated authority is in the current user's root store.
        AssertNotTrusted(_authority.Thumbprint);
        AssertNotTrusted(_otherAuthority!.Thumbprint);
    }

    [Fact]
    public async Task AnAnchorNamingAnotherAuthorityRefusesTheSourceAsUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var logs = new LogCapture();
        var otherAnchorPath = _otherAuthority!.WritePem(Path.Combine(_directory, "other-anchor"));
        _worker = await StartWorkerAsync(otherAnchorPath, logs, cancellationToken);

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(() => TakeAsync(Version, cancellationToken));

        var cause = ApplicationFailure(failure);
        Assert.Equal(TemplateTakeCodes.TemplateSourceUnavailable, cause.ErrorType);
        Assert.False(cause.NonRetryable);
        Assert.Null(await RowOrNullAsync(Version, cancellationToken));
        Assert.Contains(logs.Lines, line => line.Contains(
            "The template source could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoAnchorRefusesTheSourceAsUnavailableOnThePlatformStore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var logs = new LogCapture();
        _worker = await StartWorkerAsync(trustAnchor: null, logs, cancellationToken);

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(() => TakeAsync(Version, cancellationToken));

        var cause = ApplicationFailure(failure);
        Assert.Equal(TemplateTakeCodes.TemplateSourceUnavailable, cause.ErrorType);
        Assert.False(cause.NonRetryable);
        Assert.Null(await RowOrNullAsync(Version, cancellationToken));
        Assert.Contains(logs.Lines, line => line.Contains(
            "The template source could not be read", StringComparison.Ordinal));
    }

    private async Task<WebApplicationFactory<PdfWorker>> StartWorkerAsync(
        string? trustAnchor,
        LogCapture logs,
        CancellationToken cancellationToken)
    {
        var worker = new WebApplicationFactory<PdfWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_engine!.HostPort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory, "storage"));
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:ApiBaseUrl", _source!.BaseUrl.AbsoluteUri);
            builder.UseSetting(
                $"{TemplateSourceOptions.SectionPath}:Repository",
                GitHubTemplateSourceStub.Owner + "/" + GitHubTemplateSourceStub.Repository);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:Branch", GitHubTemplateSourceStub.Branch);
            if (trustAnchor is not null)
            {
                builder.UseSetting($"{TemplateSourceOptions.SectionPath}:TrustAnchor", trustAnchor);
            }

            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        });
        using var health = worker.CreateClient();
        await Wait.UntilAsync(
            $"/health answering {(int)HttpStatusCode.OK}",
            async token =>
            {
                using var response = await health.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            Deadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);
        return worker;
    }

    private async Task<TakeTemplateVersionResult> TakeAsync(string version, CancellationToken cancellationToken) =>
        await _client!
            .ExecuteWorkflowAsync(
                (TakeVersionForTestWorkflow workflow) => workflow.RunAsync(
                    new TakeTemplateVersionInput(Template, version, Owner, "job-1")),
                new WorkflowOptions($"trust-{Guid.NewGuid():N}", TestQueue))
            .WaitAsync(cancellationToken);

    private static ApplicationFailureException ApplicationFailure(WorkflowFailedException failure)
    {
        for (Exception? exception = failure; exception is not null; exception = exception.InnerException)
        {
            if (exception is ApplicationFailureException application)
            {
                return application;
            }
        }

        throw new Xunit.Sdk.XunitException($"the workflow failed without an application failure: {failure.Message}");
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static void AssertNotTrusted(string thumbprint)    {
        var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        try
        {
            Assert.DoesNotContain(
                store.Certificates.Cast<X509Certificate2>(),
                certificate => string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            store.Close();
        }
    }

    private async Task<StoredVersion> RowAsync(string version, CancellationToken cancellationToken) =>
        await RowOrNullAsync(version, cancellationToken)
        ?? throw new Xunit.Sdk.XunitException($"no record was written for {Template} {version}");

    private async Task<StoredVersion?> RowOrNullAsync(string version, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<StoredVersion>(new CommandDefinition(
            """
            SELECT template_id AS "TemplateId", version AS "Version", owner_consumer_id AS "OwnerConsumerId",
                   engine AS "Engine", commit_sha AS "CommitSha", package_sha256 AS "PackageSha256",
                   object_key AS "ObjectKey"
              FROM report_service.template_versions
             WHERE template_id = @TemplateId AND version = @Version
            """,
            new { TemplateId = Template, Version = version },
            cancellationToken: cancellationToken));
    }

    /// <summary>One record as the table holds it.</summary>
    private sealed class StoredVersion
    {
        public string TemplateId { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string OwnerConsumerId { get; set; } = string.Empty;

        public string Engine { get; set; } = string.Empty;

        public string CommitSha { get; set; } = string.Empty;

        public string PackageSha256 { get; set; } = string.Empty;

        public string ObjectKey { get; set; } = string.Empty;
    }
}
