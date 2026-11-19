using Dapper;
using Npgsql;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.Testing;
using System.Text.Json.Nodes;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The additive migration that records what produced a printed block's presentation, against a
/// real PostgreSQL 18.6: a database brought to the previous version, holding the commit rows the
/// earlier product wrote, is brought to the new one, every row keeps its values, and each
/// row that predates the column carries the one value that says so.
/// The migrated column then admits exactly two shapes - the complete object and that value -
/// refuses a partial object and a member carrying JSON null, and an insert that does not name
/// the column takes the default, which is what keeps a reverted build working.
/// </summary>
public sealed class PresentationProvenanceMigrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Presentation = "0015_presentation-provenance.sql";

    private const string Unrecorded = """{"source": "unrecorded"}""";

    private const string PackageSha256 = "5e88e6f1b5f8d8f0eaa2b2a4b6c8d0e2f4a6b8c0d2e4f6a8b0c2d4e6f8a0b2c4";

    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    private string _connectionString = string.Empty;
    private NpgsqlDataSource? _dataSource;
    private string _outputId = string.Empty;
    private PartRow _preChange = null!;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public PresentationProvenanceMigrationTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = _fixture.Application(database);

        // The boundary of the change: the presentation script's place in the shipped order, so
        // the pre-change state is every script before it and the rest is the change itself.
        var catalog = MigrationCatalog.Scripts.ToArray();
        var boundary = Array.FindIndex(catalog, script => string.Equals(script.Name, Presentation, StringComparison.Ordinal));
        Assert.True(boundary > 0, $"the shipped set carries '{Presentation}' after the scripts that precede it");

        var previous = catalog[..boundary];
        var before = await MigrationRunner.RunAsync(_connectionString, previous, cancellationToken);
        Assert.True(before.Succeeded, string.Join("; ", before.ErrorLines));
        Assert.Equal(previous[^1].Name, before.CurrentVersion);

        _dataSource = NpgsqlDataSource.Create(_connectionString);
        _outputId = "output-" + Guid.NewGuid().ToString("N");
        var jobId = await NewJobAsync(cancellationToken);
        await ExecuteAsync(
            """
            INSERT INTO report_service.report_outputs (output_id, report_job_id, format, status)
            VALUES (@OutputId, @ReportJobId, 'PDF', 'RUNNING')
            """,
            new { OutputId = _outputId, ReportJobId = jobId },
            cancellationToken);
        await ExecuteAsync(
            """
            INSERT INTO report_service.pdf_part_commits
                (output_id, block_index, object_key, sha256, size_bytes, page_count,
                 template_package_sha256, committed_at)
            VALUES
                (@OutputId, 0, 'outputs/' || @OutputId || '/0/attempt', @Sha256, 4096, 3, @PackageSha256, @Now)
            """,
            new { OutputId = _outputId, Sha256 = new string('a', 64), PackageSha256, Now = Now.UtcDateTime },
            cancellationToken);
        _preChange = await PartAsync(0, cancellationToken, beforeTheColumn: true);

        // The migration itself: the presentation script and every script shipped after it.
        var rest = catalog[boundary..];
        var migration = await MigrationRunner.RunAsync(_connectionString, catalog, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        Assert.Equal(Presentation, migration.Applied[0]);
        Assert.Equal(rest.Select(script => script.Name), migration.Applied);
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
    public async Task APreChangeCommitSurvivesUnchangedAndCarriesTheValueThatSaysSo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var after = await PartAsync(0, cancellationToken);

        Assert.Equal(
            _preChange with { Presentation = null, Xmin = string.Empty },
            after with { Presentation = null, Xmin = string.Empty });
        Assert.Equal(Unrecorded, after.Presentation);
        Assert.Null(PresentationProvenance.Parse(after.Presentation));
    }

    [Fact]
    public async Task TheColumnAdmitsTheCompleteObjectAndRefusesAPartialOneOrAJsonNull()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // The complete object the product writes.
        var complete = new PresentationProvenance(
            "es-AR",
            "America/Argentina/Buenos_Aires",
            new DateTimeOffset(2026, 3, 1, 12, 30, 0, TimeSpan.Zero),
            "scriban",
            "7.5.1",
            "74.2",
            "2025b").ToJson();
        await SetPresentationAsync(0, complete, cancellationToken);
        var stored = await PartAsync(0, cancellationToken);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(complete), JsonNode.Parse(stored.Presentation!)));
        Assert.Equal(
            new PresentationProvenance("es-AR", "America/Argentina/Buenos_Aires", new DateTimeOffset(2026, 3, 1, 12, 30, 0, TimeSpan.Zero), "scriban", "7.5.1", "74.2", "2025b"),
            PresentationProvenance.Parse(stored.Presentation));

        // A source-only object is not the backfill value, however much it looks like it.
        var sourceOnly = await Assert.ThrowsAsync<PostgresException>(() =>
            SetPresentationAsync(0, """{"source":"unrecorded","culture":"es-AR"}""", cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, sourceOnly.SqlState);
        Assert.Equal("pdf_part_commits_presentation_complete", sourceOnly.ConstraintName);

        // One member missing.
        var missing = await Assert.ThrowsAsync<PostgresException>(() => SetPresentationAsync(
            0,
            """{"culture":"es-AR","timeZone":"America/Argentina/Buenos_Aires","clock":"2026-03-01T12:30:00.0000000+00:00","engine":"scriban","engineVersion":"7.5.1","icuVersion":"74.2"}""",
            cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, missing.SqlState);
        Assert.Equal("pdf_part_commits_presentation_complete", missing.ConstraintName);

        // A member carrying JSON null: the check must be false, never unknown.
        var nullMember = await Assert.ThrowsAsync<PostgresException>(() => SetPresentationAsync(
            0,
            """{"culture":null,"timeZone":"America/Argentina/Buenos_Aires","clock":"2026-03-01T12:30:00.0000000+00:00","engine":"scriban","engineVersion":"7.5.1","icuVersion":"74.2","tzDataVersion":"2025b"}""",
            cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, nullMember.SqlState);
        Assert.Equal("pdf_part_commits_presentation_complete", nullMember.ConstraintName);

        // A member of the wrong kind.
        var wrongKind = await Assert.ThrowsAsync<PostgresException>(() => SetPresentationAsync(
            0,
            """{"culture":"es-AR","timeZone":"America/Argentina/Buenos_Aires","clock":12,"engine":"scriban","engineVersion":"7.5.1","icuVersion":"74.2","tzDataVersion":"2025b"}""",
            cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, wrongKind.SqlState);

        // The refused writes changed nothing: the row still carries the complete object.
        var unchanged = await PartAsync(0, cancellationToken);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(complete), JsonNode.Parse(unchanged.Presentation!)));
    }

    [Fact]
    public async Task AnInsertThatDoesNotNameTheColumnTakesTheDefault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await ExecuteAsync(
            """
            INSERT INTO report_service.pdf_part_commits
                (output_id, block_index, object_key, sha256, size_bytes, page_count,
                 template_package_sha256, committed_at)
            VALUES
                (@OutputId, 1, 'outputs/' || @OutputId || '/1/attempt', @Sha256, 2048, 1, @PackageSha256, @Now)
            """,
            new { OutputId = _outputId, Sha256 = new string('b', 64), PackageSha256, Now = Now.UtcDateTime },
            cancellationToken);

        var row = await PartAsync(1, cancellationToken);
        Assert.Equal(Unrecorded, row.Presentation);

        // The column is never absent, so a reader never sees a block without its shape.
        var nothing = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE report_service.pdf_part_commits SET presentation = NULL WHERE output_id = @OutputId AND block_index = 1",
            new { OutputId = _outputId },
            cancellationToken));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, nothing.SqlState);
    }

    private async Task<string> NewJobAsync(CancellationToken cancellationToken)
    {
        var jobId = "job-" + Guid.NewGuid().ToString("N");
        var uploadId = "upload-" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(
            """
            INSERT INTO report_service.uploads
                (upload_id, consumer_id, mode, declared_profile, status, staging_key, created_at, expires_at, completed_at)
            VALUES
                (@UploadId, 'dev-consumer', 'API_STREAM', 'HEADER_ITEMS_V1', 'READY', @StagingKey, @Now, @ExpiresAt, @Now)
            """,
            new
            {
                UploadId = uploadId,
                StagingKey = "staging/" + uploadId + "/content",
                Now = Now.UtcDateTime,
                ExpiresAt = Now.AddHours(1).UtcDateTime,
            },
            cancellationToken);
        await ExecuteAsync(
            """
            INSERT INTO report_service.report_jobs
                (report_job_id, consumer_id, upload_id, status, report_clock, created_at)
            VALUES (@ReportJobId, 'dev-consumer', @UploadId, 'RUNNING', @Now, @Now)
            """,
            new { ReportJobId = jobId, UploadId = uploadId, Now = Now.UtcDateTime },
            cancellationToken);
        return jobId;
    }

    private Task SetPresentationAsync(int blockIndex, string json, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "UPDATE report_service.pdf_part_commits SET presentation = CAST(@Json AS jsonb) WHERE output_id = @OutputId AND block_index = @BlockIndex",
            new { Json = json, OutputId = _outputId, BlockIndex = blockIndex },
            cancellationToken);

    private async Task<PartRow> PartAsync(int blockIndex, CancellationToken cancellationToken, bool beforeTheColumn = false)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        var presentation = beforeTheColumn ? "NULL::text" : "presentation::text";
        return await connection.QuerySingleAsync<PartRow>(new CommandDefinition(
            $"""
            SELECT output_id        AS "OutputId",
                   block_index      AS "BlockIndex",
                   object_key       AS "ObjectKey",
                   sha256           AS "Sha256",
                   size_bytes       AS "SizeBytes",
                   page_count       AS "PageCount",
                   template_package_sha256 AS "TemplatePackageSha256",
                   committed_at     AS "CommittedAt",
                   {presentation} AS "Presentation",
                   xmin::text       AS "Xmin"
              FROM report_service.pdf_part_commits
             WHERE output_id = @OutputId AND block_index = @BlockIndex
            """,
            new { OutputId = _outputId, BlockIndex = blockIndex },
            cancellationToken: cancellationToken));
    }

    private async Task ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    /// <summary>One commit row, in the columns the earlier product wrote and the one this change adds.</summary>
    private sealed record PartRow
    {
        public string OutputId { get; init; } = string.Empty;

        public int BlockIndex { get; init; }

        public string ObjectKey { get; init; } = string.Empty;

        public string Sha256 { get; init; } = string.Empty;

        public long SizeBytes { get; init; }

        public int PageCount { get; init; }

        public string TemplatePackageSha256 { get; init; } = string.Empty;

        public DateTime CommittedAt { get; init; }

        public string? Presentation { get; init; }

        public string Xmin { get; init; } = string.Empty;
    }
}
