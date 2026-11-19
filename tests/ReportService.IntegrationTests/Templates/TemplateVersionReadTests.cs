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
/// The metadata read against the record a take wrote, on real PostgreSQL and the
/// API's own host: its owner reads the ten members the version is described by,
/// with the publication provenance a storage-taken version carries and with a
/// pre-change version's null provenance; a version flagged shared is read by every
/// registered consumer with that same answer, while another consumer's non-shared
/// version, an untaken version and a malformed one are answered with the same
/// not-found document - the request's own trace identifier aside, and compared on
/// every header's name and value as well - without a storage read; and a consumer
/// the deployment does not know is refused before anything is read.
/// </summary>
public sealed class TemplateVersionReadTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Owner = "billing-system";
    private const string Other = "reporting-portal";
    private const string Inactive = "consumer-c";
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
    public async Task TheOwnerReadsTheTenMembersOfTheVersionItOwns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = await GivenTakenVersionAsync(cancellationToken, storageTaken: true, version: "1.2.0");

        using var response = await GetAsync(record.TemplateId, record.Version, Owner, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(record.TemplateId, root.GetProperty("templateId").GetString());
        Assert.Equal(record.Version, root.GetProperty("version").GetString());
        Assert.Equal("sha256:" + record.SourceDescriptorSha256, root.GetProperty("sourceDescriptorHash").GetString());
        var objects = root.GetProperty("objects").EnumerateArray().ToArray();
        Assert.Equal(record.SourceObjects!.Length, objects.Length);
        for (var index = 0; index < objects.Length; index++)
        {
            Assert.Equal(record.SourceObjects[index].Path, objects[index].GetProperty("path").GetString());
            Assert.Equal(record.SourceObjects[index].Size, objects[index].GetProperty("size").GetInt64());
            Assert.Equal(record.SourceObjects[index].Sha256, objects[index].GetProperty("sha256").GetString());
        }

        Assert.Equal("sha256:" + record.PackageSha256, root.GetProperty("packageHash").GetString());
        Assert.Equal("SCRIBAN", root.GetProperty("engine").GetString());
        Assert.Equal(record.EngineVersion, root.GetProperty("engineVersion").GetString());
        Assert.Equal(record.OwnerConsumerId, root.GetProperty("owner").GetString());
        Assert.False(root.GetProperty("shared").GetBoolean());
        Assert.Equal(
            record.TakenAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            root.GetProperty("takenAt").GetString());
        Assert.Equal(10, root.EnumerateObject().Count());
        Assert.False(root.TryGetProperty("commitSha", out _));
    }

    [Fact]
    public async Task APreChangeVersionAnswersWithoutACommitAndWithoutPublicationProvenance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = await GivenTakenVersionAsync(cancellationToken);

        using var response = await GetAsync(record.TemplateId, record.Version, Owner, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(10, root.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sourceDescriptorHash").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("objects").ValueKind);
        Assert.False(root.TryGetProperty("commitSha", out _));
        Assert.Equal("sha256:" + record.PackageSha256, root.GetProperty("packageHash").GetString());
        Assert.Equal(record.OwnerConsumerId, root.GetProperty("owner").GetString());
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

    [Theory]
    [InlineData(Owner, true, HttpStatusCode.OK)]
    [InlineData(Owner, false, HttpStatusCode.OK)]
    [InlineData(Other, true, HttpStatusCode.OK)]
    [InlineData(Other, false, HttpStatusCode.NotFound)]
    public async Task TheFourCombinationsOfOwnerAndSharingAnswerAsTheRuleSays(
        string consumer,
        bool shared,
        HttpStatusCode expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = await GivenTakenVersionAsync(cancellationToken, shared: shared);

        using var response = await GetAsync(record.TemplateId, record.Version, consumer, cancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ASharedVersionIsVisibleToEveryConsumerWithTheOwnersAnswer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = await GivenTakenVersionAsync(cancellationToken, storageTaken: true, shared: true);

        using var byOwner = await GetAsync(record.TemplateId, record.Version, Owner, cancellationToken);
        using var byOther = await GetAsync(record.TemplateId, record.Version, Other, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, byOwner.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byOther.StatusCode);

        var ownerBody = await byOwner.Content.ReadAsStringAsync(cancellationToken);
        var otherBody = await byOther.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(ownerBody, otherBody);

        using var document = JsonDocument.Parse(otherBody);
        var root = document.RootElement;
        Assert.Equal(10, root.EnumerateObject().Count());
        Assert.Equal(record.TemplateId, root.GetProperty("templateId").GetString());
        Assert.Equal(record.Version, root.GetProperty("version").GetString());
        Assert.Equal("sha256:" + record.SourceDescriptorSha256, root.GetProperty("sourceDescriptorHash").GetString());
        var objects = root.GetProperty("objects").EnumerateArray().ToArray();
        Assert.Equal(record.SourceObjects!.Length, objects.Length);
        for (var index = 0; index < objects.Length; index++)
        {
            Assert.Equal(record.SourceObjects[index].Path, objects[index].GetProperty("path").GetString());
            Assert.Equal(record.SourceObjects[index].Size, objects[index].GetProperty("size").GetInt64());
            Assert.Equal(record.SourceObjects[index].Sha256, objects[index].GetProperty("sha256").GetString());
        }

        Assert.Equal("sha256:" + record.PackageSha256, root.GetProperty("packageHash").GetString());
        Assert.Equal(record.OwnerConsumerId, root.GetProperty("owner").GetString());
        Assert.True(root.GetProperty("shared").GetBoolean());
        Assert.False(root.TryGetProperty("commitSha", out _));
    }

    [Fact]
    public async Task ASharedVersionIsNotAnsweredAsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = await GivenTakenVersionAsync(cancellationToken, storageTaken: true, shared: true);

        using var shared = await GetAsync(record.TemplateId, record.Version, Other, cancellationToken);
        using var absent = await GetAsync("no-such-template", "9.9.9", Other, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);

        // The envelope - the status and every header's name and value, the clock aside - and the
        // document are the shared answer's own: neither belongs to the one not-found answer.
        Assert.NotEqual(EnvelopeOf(absent), EnvelopeOf(shared));
        Assert.DoesNotContain(
            "TEMPLATE_NOT_FOUND",
            await shared.Content.ReadAsStringAsync(cancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoVersionsOfOneTemplateAnswerDifferentlyWhenOneIsSharedAndTheOtherIsNot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await GivenTakenVersionAsync(cancellationToken, version: "1.0.0", shared: false);
        await GivenTakenVersionAsync(cancellationToken, version: "1.1.0", shared: true);

        using var privateVersion = await GetAsync("invoice-energy", "1.0.0", Other, cancellationToken);
        using var sharedVersion = await GetAsync("invoice-energy", "1.1.0", Other, cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, privateVersion.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sharedVersion.StatusCode);
    }

    [Fact]
    public async Task AnInactiveConsumerReadsASharedVersionAndItsOwnButNotAnotherConsumersPrivateOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await GivenTakenVersionAsync(cancellationToken, version: "1.1.0", shared: true);
        await GivenTakenVersionAsync(cancellationToken, version: "1.0.0", shared: false);
        await GivenTakenVersionAsync(cancellationToken, version: "2.0.0", owner: Inactive);

        using var shared = await GetAsync("invoice-energy", "1.1.0", Inactive, cancellationToken);
        using var privateVersion = await GetAsync("invoice-energy", "1.0.0", Inactive, cancellationToken);
        using var own = await GetAsync("invoice-energy", "2.0.0", Inactive, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, privateVersion.StatusCode);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task ASharedVersionIsAddressedByItsExactBuildMetadataAndNotByItsPlainVersion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await GivenTakenVersionAsync(cancellationToken, shared: true, version: "1.0.0+build.1");

        using var exact = await GetAsync("invoice-energy", "1.0.0+build.1", Other, cancellationToken);
        using var plain = await GetAsync("invoice-energy", "1.0.0", Other, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, plain.StatusCode);
    }

    [Fact]
    public async Task APreChangeSharedVersionAnswersWithoutProvenanceToEveryConsumer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = await GivenTakenVersionAsync(cancellationToken, storageTaken: false, shared: true);

        using var response = await GetAsync(record.TemplateId, record.Version, Other, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(10, root.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sourceDescriptorHash").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("objects").ValueKind);
        Assert.False(root.TryGetProperty("commitSha", out _));
        Assert.True(root.GetProperty("shared").GetBoolean());
        Assert.Equal(record.OwnerConsumerId, root.GetProperty("owner").GetString());
    }

    private async Task<TemplateVersionRecord> GivenTakenVersionAsync(
        CancellationToken cancellationToken,
        bool storageTaken = false,
        string version = "1.1.0",
        bool shared = false,
        string owner = Owner)
    {
        var record = storageTaken
            ? TemplateVersionRecords.StorageSample(
                "invoice-energy",
                version,
                owner: owner,
                shared: shared,
                takenAt: new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero))
            : TemplateVersionRecords.Sample(
                "invoice-energy",
                version,
                owner: owner,
                shared: shared,
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

    // The answer's envelope as one comparable document: the status and every header's name and
    // value, the clock aside. Two answers are the same answer only when neither the status nor a
    // header differs; the length is included with the value the server sent.
    private static string EnvelopeOf(HttpResponseMessage response) =>
        $"{(int)response.StatusCode};" + string.Join(
            ';',
            response.Headers.Concat(response.Content.Headers)
                .Where(header => !string.Equals(header.Key, "Date", StringComparison.OrdinalIgnoreCase))
                .OrderBy(header => header.Key, StringComparer.Ordinal)
                .Select(header => header.Key + "=" + string.Join(",", header.Value)));

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
            { "consumers": [ { "consumerId": "{{Owner}}", "active": true }, { "consumerId": "{{Other}}", "active": true }, { "consumerId": "{{Inactive}}", "active": false } ] }
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
