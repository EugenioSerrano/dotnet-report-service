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
/// answered with the same not-found document - the request's own trace identifier
/// aside - without a storage read; and a consumer the deployment does not know is
/// refused before anything is read.
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

        var answers = new[] { other, untaken, malformed };
        var bodies = new string[answers.Length];
        var comparable = new string[answers.Length];
        var traceIds = new string[answers.Length];
        for (var index = 0; index < answers.Length; index++)
        {
            bodies[index] = await answers[index].Content.ReadAsStringAsync(cancellationToken);
            comparable[index] = WithoutTraceIdentifier(bodies[index], out traceIds[index]);
        }

        Assert.All(answers, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.All(answers, response => Assert.Equal(ApiProblem.ContentType, response.Content.Headers.ContentType?.MediaType));
        Assert.All(bodies, body => Assert.Contains("\"code\":\"TEMPLATE_NOT_FOUND\"", body, StringComparison.Ordinal));
        Assert.All(bodies, body => Assert.Equal(AnswerMembers, MembersOf(body)));

        // One condition, one answer. The request's own trace identifier is present and different
        // for each request, and it is the only member that is not the condition's, so it is
        // replaced before the documents are compared: the three answers are then one document.
        Assert.Equal(answers.Length, traceIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Single(comparable.Distinct(StringComparer.Ordinal));
        Assert.Single(answers.Select(EnvelopeOf).Distinct(StringComparer.Ordinal));

        // And the document is the documented one: a comparison that would pass for any three
        // 404s would prove nothing about this condition.
        using var document = JsonDocument.Parse(bodies[0]);
        var answer = document.RootElement;
        Assert.Equal("about:blank", answer.GetProperty("type").GetString());
        Assert.Equal("Template not found", answer.GetProperty("title").GetString());
        Assert.Equal((int)HttpStatusCode.NotFound, answer.GetProperty("status").GetInt32());
        Assert.Equal("The template version was not found.", answer.GetProperty("detail").GetString());
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

    // The members of a not-found answer: what the condition says and the request's own
    // identifier, and nothing that could tell the three conditions apart.
    private static readonly string[] AnswerMembers =
        ["code", "detail", "status", "title", "traceId", "type"];

    private static string[] MembersOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.EnumerateObject()
            .Select(member => member.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    // The headers of the answer, the clock aside. The length follows the one member that is the
    // request's own, so it is compared by name and never by value.
    private static string EnvelopeOf(HttpResponseMessage response) =>
        string.Join(
            ';',
            response.Headers.Concat(response.Content.Headers)
                .Select(header => header.Key)
                .Where(name => !string.Equals(name, "Date", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal));

    // The request's own trace identifier: present in every answer, different for every request,
    // and the one member that is not the condition's. It is replaced by one fixed text so that
    // the answers are compared member for member on the condition they describe.
    private static string WithoutTraceIdentifier(string body, out string traceId)
    {
        using var document = JsonDocument.Parse(body);
        Assert.True(
            document.RootElement.TryGetProperty("traceId", out var member),
            "the answer carries no trace identifier");
        Assert.Equal(JsonValueKind.String, member.ValueKind);
        traceId = member.GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(traceId), "the answer's trace identifier is empty");

        var present = "\"traceId\":\"" + traceId + "\"";
        Assert.Contains(present, body, StringComparison.Ordinal);
        return body.Replace(present, "\"traceId\":\"<the request's own>\"", StringComparison.Ordinal);
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
