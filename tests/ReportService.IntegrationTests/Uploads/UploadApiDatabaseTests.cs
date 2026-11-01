using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Uploads;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.Testing;

namespace ReportService.IntegrationTests.Uploads;

/// <summary>
/// The API over a real PostgreSQL server: the session a consumer opens is written
/// once, with its owner and the key its bytes will receive, and read back
/// unchanged; a request this increment does not serve leaves the table as it was;
/// and a server that stops answering is reported as a retryable condition, after
/// which the same process creates the next session normally.
/// </summary>
public sealed class UploadApiDatabaseTests : IClassFixture<PostgresFixture>, IDisposable
{
    private const string Consumer = "dev-consumer";
    private const string ValidBody = """{"profile":"header-items-v1","mode":"API_STREAM"}""";

    private readonly PostgresFixture _fixture;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-upload-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    private readonly List<IDisposable> _disposables = [];

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public UploadApiDatabaseTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ACreatedSessionIsStoredOnceAndReadBackIdentically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (database, connectionString) = await MigratedDatabaseAsync(cancellationToken);
        await using var api = Api(connectionString);
        using var client = api.CreateClient();

        using var created = await client.SendAsync(CreateRequest(Json(ValidBody)), cancellationToken);
        var createdBody = await created.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var projection = JsonDocument.Parse(createdBody);
        var uploadId = projection.RootElement.GetProperty("uploadId").GetString()!;
        var row = Assert.Single(await RowsAsync(database, cancellationToken));
        Assert.Equal(uploadId, row.UploadId);
        Assert.Equal(Consumer, row.ConsumerId);
        Assert.Equal("API_STREAM", row.Mode);
        Assert.Equal("HEADER_ITEMS_V1", row.Profile);
        Assert.Equal("CREATED", row.Status);
        Assert.Equal($"staging/{uploadId}/content", row.StagingKey);
        Assert.Equal(
            projection.RootElement.GetProperty("createdAt").GetString(),
            UploadWireValues.FormatTimestamp(new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc), TimeSpan.Zero)));
        Assert.Equal(
            projection.RootElement.GetProperty("expiresAt").GetString(),
            UploadWireValues.FormatTimestamp(new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc), TimeSpan.Zero)));

        using var read = await client.SendAsync(GetRequest(uploadId), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(createdBody, await read.Content.ReadAsStringAsync(cancellationToken));
    }

    [Theory]
    [InlineData("DIRECT_STORAGE", "header-items-v1")]
    [InlineData("API_STREAM", "header-items-v2")]
    public async Task ARequestThisIncrementDoesNotServeLeavesTheTableUnchanged(string mode, string profile)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (database, connectionString) = await MigratedDatabaseAsync(cancellationToken);
        await using var api = Api(connectionString);
        using var client = api.CreateClient();
        var body = $$"""{"profile":"{{profile}}","mode":"{{mode}}"}""";

        using var response = await client.SendAsync(CreateRequest(Json(body)), cancellationToken);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).RootElement;

        Assert.Equal(422, (int)response.StatusCode);
        Assert.Equal(ErrorCodes.UnsupportedCapability, problem.GetProperty("code").GetString());
        Assert.Empty(await RowsAsync(database, cancellationToken));
    }

    [Fact]
    public async Task AStoppedServerIsReportedAsUnavailableAndTheSameProcessCreatesAgain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (database, connectionString) = await MigratedDatabaseAsync(cancellationToken);
        await using var api = Api(connectionString);
        using var client = api.CreateClient();

        string firstUploadId;
        using (var first = await client.SendAsync(CreateRequest(Json(ValidBody)), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            firstUploadId = await UploadIdAsync(first, cancellationToken);
        }

        try
        {
            await _fixture.PauseAsync(cancellationToken);
            using var refused = await client.SendAsync(CreateRequest(Json(ValidBody)), cancellationToken);
            var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(cancellationToken)).RootElement;

            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal(ErrorCodes.ServiceUnavailable, problem.GetProperty("code").GetString());
            Assert.DoesNotContain(_fixture.RolePassword, problem.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            await _fixture.UnpauseAsync(cancellationToken);
        }

        // The store holds no broken state: the next request of the same process creates its session.
        using var after = await client.SendAsync(CreateRequest(Json(ValidBody)), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, after.StatusCode);
        var afterUploadId = await UploadIdAsync(after, cancellationToken);

        // The refused request's statement had already reached the server, and a statement that
        // lost its client while the server was stopped is still executed once it answers again.
        // Whether the table therefore holds two rows or three is not something this case decides,
        // so it asserts what either outcome must show: every stored row belongs to this consumer,
        // is in the state a fresh session has and carries the key its identifier promises, and
        // each session the consumer was given back is one of those rows, exactly once.
        var rows = await RowsAsync(database, cancellationToken);
        Assert.NotEqual(firstUploadId, afterUploadId);
        Assert.All(rows, row =>
        {
            Assert.Equal(Consumer, row.ConsumerId);
            Assert.Equal("CREATED", row.Status);
            Assert.Equal($"staging/{row.UploadId}/content", row.StagingKey);
        });
        Assert.Single(rows, row => row.UploadId == firstUploadId);
        Assert.Single(rows, row => row.UploadId == afterUploadId);
    }

    private static async Task<string> UploadIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return body.RootElement.GetProperty("uploadId").GetString()!;
    }

    // A database of its own for every case, brought to the version the code expects, so no case
    // sees a row another case wrote and a table's contents are only this case's own.
    private async Task<(string Database, string ConnectionString)> MigratedDatabaseAsync(CancellationToken cancellationToken)
    {
        var database = await _fixture.CreateDatabaseAsync(cancellationToken);
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(database))
        {
            Timeout = 5,
            CommandTimeout = PostgresUploadStore.CommandTimeoutSeconds,
        }.ConnectionString;
        var migration = await MigrationRunner.RunAsync(connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        return (database, connectionString);
    }

    private WebApplicationFactory<Program> Api(string connectionString)
    {
        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(registry, """{ "consumers": [ { "consumerId": "dev-consumer", "active": true } ] }""");
        var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory, "storage"));
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
        });
        _disposables.Add(api);
        return api;
    }

    private async Task<IReadOnlyList<UploadRow>> RowsAsync(string database, CancellationToken cancellationToken)
    {
        await using var connection = await _fixture.OpenAsync(database, cancellationToken);
        return
        [
            .. await connection.QueryAsync<UploadRow>(new CommandDefinition(
                """
                SELECT upload_id AS "UploadId", consumer_id AS "ConsumerId", mode AS "Mode",
                       declared_profile AS "Profile", status AS "Status", staging_key AS "StagingKey",
                       created_at AS "CreatedAt", expires_at AS "ExpiresAt"
                FROM report_service.uploads
                ORDER BY created_at
                """,
                cancellationToken: cancellationToken)),
        ];
    }

    private static HttpRequestMessage CreateRequest(HttpContent content)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
        {
            Content = content,
        };
        message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        return message;
    }

    private static HttpRequestMessage GetRequest(string uploadId)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}", UriKind.Relative));
        message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        return message;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>One stored session, as the table holds it.</summary>
    private sealed class UploadRow
    {
        public string UploadId { get; set; } = string.Empty;

        public string ConsumerId { get; set; } = string.Empty;

        public string Mode { get; set; } = string.Empty;

        public string Profile { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string StagingKey { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        public DateTime ExpiresAt { get; set; }
    }
}
