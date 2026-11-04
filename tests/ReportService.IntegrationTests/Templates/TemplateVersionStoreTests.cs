using Dapper;
using Npgsql;
using ReportService.Migrations;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;

namespace ReportService.IntegrationTests.Templates;

/// <summary>
/// The taken-version record against a real PostgreSQL 18.6 with the product's
/// migrations applied: a version is recorded once even when eight takes of it
/// insert at the same moment, and the losers read the winner; the table's own
/// checks refuse a row the product can never write; the manifest survives the
/// round trip through <c>jsonb</c>, numbers and all; and the pin the next change
/// adds compiles against the table it pins to.
/// </summary>
public sealed class TemplateVersionStoreTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private string _connectionString = string.Empty;

    private NpgsqlDataSource? _dataSource;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public TemplateVersionStoreTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = _fixture.Application(database);
        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(_connectionString);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    [Fact]
    public async Task EightTakesOfOneVersionInsertAtOnceAndExactlyOneWins()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new PostgresTemplateVersionStore(_dataSource!);
        var record = Record("concurrent");

        var inserts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => InsertAsync(store, record, cancellationToken)));

        Assert.Equal(1, inserts.Count(insert => insert.WasInserted));
        Assert.Equal(7, inserts.Count(insert => !insert.WasInserted));
        Assert.All(inserts, insert => Assert.Equal(record.TemplateId, insert.Record.TemplateId));
        Assert.All(inserts, insert => Assert.Equal(record.PackageSha256, insert.Record.PackageSha256));
        Assert.Equal(1, await ScalarAsync(cancellationToken, "SELECT count(*) FROM report_service.template_versions"));
    }

    [Fact]
    public async Task ARecordedVersionReadsBackWithItsManifestWhole()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new PostgresTemplateVersionStore(_dataSource!);
        var record = Record("round-trip");

        Assert.True((await store.TryInsertAsync(record, cancellationToken)).WasInserted);

        var read = Assert.IsType<TemplateVersionRecord>(await store.FindAsync(record.TemplateId, record.Version, cancellationToken));
        Assert.Equal(record.TemplateId, read.TemplateId);
        Assert.Equal(record.Version, read.Version);
        Assert.Equal(record.OwnerConsumerId, read.OwnerConsumerId);
        Assert.Equal(record.Shared, read.Shared);
        Assert.Equal(record.Engine, read.Engine);
        Assert.Equal(record.EngineVersion, read.EngineVersion);
        Assert.Equal(record.CommitSha, read.CommitSha);
        Assert.Equal(record.PackageSha256, read.PackageSha256);
        Assert.Equal(record.PackageSize, read.PackageSize);
        Assert.Equal(record.FileCount, read.FileCount);
        Assert.Equal(record.ObjectKey, read.ObjectKey);
        Assert.Equal(record.ManifestJson, read.ManifestJson);
        Assert.Equal(record.Manifest.Outputs?.Pdf?.MarginMm?.Top, read.Manifest.Outputs?.Pdf?.MarginMm?.Top);
        Assert.Equal(record.Manifest.Outputs?.Pdf?.PageTotals?.Scale, read.Manifest.Outputs?.Pdf?.PageTotals?.Scale);
        Assert.Equal(record.TakenAt.ToUniversalTime(), read.TakenAt.ToUniversalTime());
    }

    [Theory]
    [InlineData("UPDATE report_service.template_versions SET engine_version = 'not-a-version'")]
    [InlineData("UPDATE report_service.template_versions SET engine = 'RAZOR'")]
    [InlineData("UPDATE report_service.template_versions SET manifest_version = '2.0'")]
    [InlineData("UPDATE report_service.template_versions SET version = 'latest'")]
    [InlineData("UPDATE report_service.template_versions SET commit_sha = 'none'")]
    [InlineData("UPDATE report_service.template_versions SET package_sha256 = 'none'")]
    [InlineData("UPDATE report_service.template_versions SET file_count = 0")]
    [InlineData("UPDATE report_service.template_versions SET package_size = 0")]
    public async Task TheTableRefusesARowTheProductCouldNeverWrite(string statement)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new PostgresTemplateVersionStore(_dataSource!);
        var record = Record("checks-" + Guid.NewGuid().ToString("N"));
        Assert.True((await store.TryInsertAsync(record, cancellationToken)).WasInserted);

        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);

        await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(statement, cancellationToken: cancellationToken)));
    }

    [Fact]
    public async Task ThePinsColumnsCompileAgainstTheTableTheyPointAt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("CREATE SCHEMA scratch_pin", cancellationToken: cancellationToken));

        // The table the pin points at, in the scratch schema, with the product's own check list.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE scratch_pin.template_references (
                template_id text NOT NULL,
                PRIMARY KEY (template_id)
            );
            CREATE TABLE scratch_pin.template_versions (LIKE report_service.template_versions INCLUDING ALL);
            """,
            cancellationToken: cancellationToken));

        // The pin of the change that writes it: every resolved column, tied to the record.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            ALTER TABLE scratch_pin.template_references
                ADD COLUMN resolved_version text        NULL,
                ADD COLUMN commit_sha       text        NULL,
                ADD COLUMN package_sha256   text        NULL,
                ADD COLUMN engine           text        NULL,
                ADD COLUMN engine_version   text        NULL,
                ADD COLUMN schema_ref       text        NULL,
                ADD COLUMN resolved_at      timestamptz NULL,
                ADD CONSTRAINT template_references_pin_version
                    FOREIGN KEY (template_id, resolved_version)
                    REFERENCES scratch_pin.template_versions (template_id, version),
                ADD CONSTRAINT template_references_pin_complete CHECK (
                    (resolved_version IS NULL) = (commit_sha IS NULL) AND
                    (resolved_version IS NULL) = (package_sha256 IS NULL) AND
                    (resolved_version IS NULL) = (engine IS NULL) AND
                    (resolved_version IS NULL) = (engine_version IS NULL) AND
                    (resolved_version IS NULL) = (schema_ref IS NULL) AND
                    (resolved_version IS NULL) = (resolved_at IS NULL));
            """,
            cancellationToken: cancellationToken));

    }

    private static async Task<TemplateVersionInsert> InsertAsync(
        PostgresTemplateVersionStore store,
        TemplateVersionRecord record,
        CancellationToken cancellationToken) =>
        await store.TryInsertAsync(record, cancellationToken);

    private static TemplateVersionRecord Record(string templateId) =>
        TemplateVersionRecords.Sample(templateId, "1.1.0", engineVersion: TemplateEngineCatalog.ScribanVersion);

    private async Task<long> ScalarAsync(CancellationToken cancellationToken, string sql)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }
}
