using System.Globalization;
using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;

namespace ReportService.IntegrationTests.Templates;

/// <summary>
/// The owner's metadata read against the record a take wrote, on real PostgreSQL
/// and the API's own host: its owner reads the nine members the version is
/// described by; another consumer, an untaken version and a malformed one are
/// answered with the identical not-found document, without a storage read; and a
/// consumer the deployment does not know is refused before anything is read.
/// </summary>
public sealed class TemplateVersionReadTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Owner = "billing-system";
    private const string Other = "reporting-portal";
    private const string Unknown = "not-a-consumer";

    private readonly PostgresFixture _fixture;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-template-read-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private string _connectionString = string.Empty;
    private NpgsqlDataSource? _dataSource;
    private WebApplicationFactory<Program>? _api;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public TemplateVersionReadTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var database = await _fixture.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        _connectionString = _fixture.Application(database);
        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, TestContext.Current.CancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(_connectionString);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
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
    public async Task TheOwnerReadsTheNineMembersOfTheVersionItOwns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = await GivenTakenVersionAsync(cancellationToken);

        using var response = await GetAsync(record.TemplateId, record.Version, Owner, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(record.TemplateId, root.GetProperty("templateId").GetString());
        Assert.Equal(record.Version, root.GetProperty("version").GetString());
        Assert.Equal(record.CommitSha, root.GetProperty("commitSha").GetString());
        Assert.Equal("sha256:" + record.PackageSha256, root.GetProperty("packageHash").GetString());
        Assert.Equal("SCRIBAN", root.GetProperty("engine").GetString());
        Assert.Equal(record.EngineVersion, root.GetProperty("engineVersion").GetString());
        Assert.Equal(record.OwnerConsumerId, root.GetProperty("owner").GetString());
        Assert.False(root.GetProperty("shared").GetBoolean());
        Assert.Equal(
            record.TakenAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            root.GetProperty("takenAt").GetString());
        Assert.Equal(9, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task AnotherConsumerAndAnUntakenVersionAreAnsweredWithTheSameNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = await GivenTakenVersionAsync(cancellationToken);

        using var other = await GetAsync(record.TemplateId, record.Version, Other, cancellationToken);
        using var untaken = await GetAsync(record.TemplateId, "9.9.9", Owner, cancellationToken);
        using var malformed = await GetAsync(record.TemplateId, "latest", Owner, cancellationToken);

        var documents = new[]
        {
            await other.Content.ReadAsStringAsync(cancellationToken),
            await untaken.Content.ReadAsStringAsync(cancellationToken),
            await malformed.Content.ReadAsStringAsync(cancellationToken),
        };

        Assert.All([other, untaken, malformed], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.All(documents, body => Assert.Contains("\"code\":\"TEMPLATE_NOT_FOUND\"", body, StringComparison.Ordinal));
        Assert.Single(documents.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AConsumerTheDeploymentDoesNotKnowIsRefusedBeforeAnythingIsRead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = await GivenTakenVersionAsync(cancellationToken);

        using var response = await GetAsync(record.TemplateId, record.Version, Unknown, cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<TemplateVersionRecord> GivenTakenVersionAsync(CancellationToken cancellationToken)
    {
        var record = TemplateVersionRecords.Sample(
            "invoice-energy",
            "1.1.0",
            owner: Owner,
            takenAt: new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new PostgresTemplateVersionStore(_dataSource!);
        Assert.True((await store.TryInsertAsync(record, cancellationToken)).WasInserted);
        return record;
    }

    private async Task<HttpResponseMessage> GetAsync(string templateId, string version, string consumer, CancellationToken cancellationToken)
    {
        _api ??= Api();
        using var client = _api.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"/api/v1/templates/{templateId}/versions/{version}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumer);
        return await client.SendAsync(request, cancellationToken);
    }

    private WebApplicationFactory<Program> Api()
    {
        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(
            registry,
            $$"""
            { "consumers": [ { "consumerId": "{{Owner}}", "active": true }, { "consumerId": "{{Other}}", "active": true } ] }
            """);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory, "storage"));
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
        });
    }
}
