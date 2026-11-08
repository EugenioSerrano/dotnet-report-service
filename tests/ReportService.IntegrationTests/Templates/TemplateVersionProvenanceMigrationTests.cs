using System.Globalization;
using Dapper;
using Npgsql;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;

namespace ReportService.IntegrationTests.Templates;

/// <summary>
/// The additive migration that lets a version and a pin name the publication they
/// were taken from, against a real PostgreSQL 18.6: a database brought to the
/// previous version, holding the rows the earlier product wrote, is brought to the
/// new one and every original value and each row's <c>xmin</c> stay exactly as
/// they were. The migrated schema then admits exactly the two shapes a taken
/// version can have and the three a pin can have, refuses every mixture by name,
/// and keeps the two new pin columns set once like the rest of the pin.
/// </summary>
public sealed class TemplateVersionProvenanceMigrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Provenance = "0011_template-version-provenance.sql";

    private const string CommitSha = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";

    private const string SourceTreeSha = "b2c3d4e5f60718293a4b5c6d7e8f90123456789a";

    private const string PackageSha256 = "5e88e6f1b5f8d8f0eaa2b2a4b6c8d0e2f4a6b8c0d2e4f6a8b0c2d4e6f8a0b2c4";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    private string _connectionString = string.Empty;
    private NpgsqlDataSource? _dataSource;
    private string _preChangeJobId = string.Empty;
    private VersionRow _versionBefore = null!;
    private ReferenceRow _pinBefore = null!;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public TemplateVersionProvenanceMigrationTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = _fixture.Application(database);

        // The database of the previous product: every script before the provenance one.
        var previous = MigrationCatalog.Scripts
            .TakeWhile(script => string.CompareOrdinal(script.Name, Provenance) < 0)
            .ToArray();
        var before = await MigrationRunner.RunAsync(_connectionString, previous, cancellationToken);
        Assert.True(before.Succeeded, string.Join("; ", before.ErrorLines));

        _dataSource = NpgsqlDataSource.Create(_connectionString);

        // The rows the earlier product wrote, recorded exactly as they are.
        await ExecuteAsync(
            """
            INSERT INTO report_service.template_versions
                (template_id, version, owner_consumer_id, shared, manifest_version, engine, engine_version,
                 schema_ref, commit_sha, source_tree_sha, package_sha256, package_size, file_count,
                 object_key, manifest, taken_at)
            VALUES
                ('pilot-grouped', '1.0.0', 'dev-consumer', false, '1.0', 'SCRIBAN', '7.5.1', 'schema.json',
                 @CommitSha, @SourceTreeSha, @PackageSha256, 4096, 4,
                 'templates/dev-consumer/pilot-grouped/1.0.0/' || @PackageSha256,
                 CAST('{"manifestVersion":"1.0","id":"pilot-grouped","version":"1.0.0"}' AS jsonb), @Now)
            """,
            new { CommitSha, SourceTreeSha, PackageSha256, Now = Now.UtcDateTime },
            cancellationToken);

        _preChangeJobId = await NewJobAsync("1.0.0", null, cancellationToken);
        await ExecuteAsync(
            """
            UPDATE report_service.template_references
               SET resolved_version = '1.0.0',
                   commit_sha = @CommitSha,
                   package_sha256 = @PackageSha256,
                   engine = 'SCRIBAN',
                   engine_version = '7.5.1',
                   schema_ref = 'schema.json',
                   resolved_at = @Now
             WHERE report_job_id = @ReportJobId
            """,
            new { CommitSha, PackageSha256, Now = Now.UtcDateTime, ReportJobId = _preChangeJobId },
            cancellationToken);

        _versionBefore = await VersionAsync("1.0.0", cancellationToken);
        _pinBefore = await ReferenceAsync(_preChangeJobId, cancellationToken);

        // The migration itself: the rest of the shipped set.
        var rest = MigrationCatalog.Scripts
            .SkipWhile(script => string.CompareOrdinal(script.Name, Provenance) < 0)
            .ToArray();
        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        Assert.Equal([Provenance], rest.Select(script => script.Name));
        Assert.Equal([Provenance], migration.Applied);
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
    public async Task APreChangeVersionAndItsPinSurviveTheMigrationUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var version = await VersionAsync("1.0.0", cancellationToken);
        Assert.Equal(_versionBefore, version);
        Assert.Equal(_versionBefore.Xmin, version.Xmin);

        var provenance = await ProvenanceAsync("1.0.0", cancellationToken);
        Assert.Null(provenance.SourceDescriptorSha256);
        Assert.Null(provenance.SourceDescriptorKey);
        Assert.Null(provenance.SourceObjectsJson);

        var reference = await ReferenceAsync(_preChangeJobId, cancellationToken);
        Assert.Equal(_pinBefore, reference);
        Assert.Equal(_pinBefore.Xmin, reference.Xmin);
        Assert.Equal(CommitSha, reference.CommitSha);

        var store = new PostgresTemplateVersionStore(_dataSource!);
        var record = await store.FindAsync("pilot-grouped", "1.0.0", cancellationToken);
        Assert.NotNull(record);
        Assert.Equal(CommitSha, record!.CommitSha);
        Assert.Equal(SourceTreeSha, record.SourceTreeSha);
        Assert.Null(record.SourceDescriptorSha256);
        Assert.Null(record.SourceDescriptorKey);
        Assert.Null(record.SourceObjects);

        var jobs = new PostgresJobStore(_dataSource!);
        var pin = Assert.IsType<TemplatePin>((await jobs.FindTemplateReferenceAsync(_preChangeJobId, cancellationToken))!.Pin);
        Assert.Equal(CommitSha, pin.CommitSha);
        Assert.Null(pin.SourceDescriptorSha256);
        Assert.Null(pin.SourceObjects);
    }

    [Fact]
    public async Task TheTableAdmitsTheTwoTakenShapesAndRefusesAMixture()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new PostgresTemplateVersionStore(_dataSource!);

        // A storage-taken row, through the product's own store.
        var storage = TemplateVersionRecords.StorageSample("pilot-grouped", "2.0.0", owner: "dev-consumer");
        Assert.True((await store.TryInsertAsync(storage, cancellationToken)).WasInserted);
        var read = await store.FindAsync("pilot-grouped", "2.0.0", cancellationToken);
        Assert.NotNull(read);
        Assert.Null(read!.CommitSha);
        Assert.Equal(storage.SourceDescriptorSha256, read.SourceDescriptorSha256);
        Assert.Equal(storage.SourceDescriptorKey, read.SourceDescriptorKey);
        Assert.Equal(storage.SourceObjects, read.SourceObjects);

        // Both shapes at once.
        var both = await Assert.ThrowsAsync<PostgresException>(() => InsertVersionAsync(
            "3.0.0",
            CommitSha,
            SourceTreeSha,
            storage.SourceDescriptorSha256,
            storage.SourceDescriptorKey,
            TemplateSourceObjects.Write(storage.SourceObjects!),
            cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, both.SqlState);
        Assert.Equal("template_versions_provenance", both.ConstraintName);

        // A commit without its tree.
        var halfTree = await Assert.ThrowsAsync<PostgresException>(() => InsertVersionAsync(
            "4.0.0",
            CommitSha,
            null,
            null,
            null,
            null,
            cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, halfTree.SqlState);
        Assert.Equal("template_versions_provenance", halfTree.ConstraintName);

        // A descriptor without the key it was read from.
        var halfKey = await Assert.ThrowsAsync<PostgresException>(() => InsertVersionAsync(
            "5.0.0",
            null,
            null,
            storage.SourceDescriptorSha256,
            null,
            TemplateSourceObjects.Write(storage.SourceObjects!),
            cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, halfKey.SqlState);
        Assert.Equal("template_versions_provenance", halfKey.ConstraintName);

        // A descriptor hash that is not 64 lower-case hexadecimal characters.
        var shortHash = await Assert.ThrowsAsync<PostgresException>(() => InsertVersionAsync(
            "6.0.0",
            null,
            null,
            PackageSha256[..63],
            storage.SourceDescriptorKey,
            TemplateSourceObjects.Write(storage.SourceObjects!),
            cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, shortHash.SqlState);
        Assert.Equal("template_versions_source_descriptor_sha256", shortHash.ConstraintName);
    }

    [Fact]
    public async Task ThePinAdmitsTheThreeShapesAndRefusesAMixture()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var jobs = new PostgresJobStore(_dataSource!);
        var store = new PostgresTemplateVersionStore(_dataSource!);
        var storage = TemplateVersionRecords.StorageSample("pilot-grouped", "2.1.0", owner: "dev-consumer");
        Assert.True((await store.TryInsertAsync(storage, cancellationToken)).WasInserted);
        var objectsJson = TemplateSourceObjects.Write(storage.SourceObjects!);

        // The absent shape is the reference as acceptance writes it.
        var absentJob = await NewJobAsync("2.1.0", null, cancellationToken);
        Assert.Null((await jobs.FindTemplateReferenceAsync(absentJob, cancellationToken))!.Pin);

        // The Git shape, written by the earlier product's own statement.
        var gitJob = await NewJobAsync("1.0.0", null, cancellationToken);
        await ExecuteAsync(
            """
            UPDATE report_service.template_references
               SET resolved_version = '1.0.0', commit_sha = @CommitSha, package_sha256 = @PackageSha256,
                   engine = 'SCRIBAN', engine_version = '7.5.1', schema_ref = 'schema.json', resolved_at = @Now
             WHERE report_job_id = @ReportJobId
            """,
            new { CommitSha, PackageSha256, Now = Now.UtcDateTime, ReportJobId = gitJob },
            cancellationToken);
        var gitPin = Assert.IsType<TemplatePin>((await jobs.FindTemplateReferenceAsync(gitJob, cancellationToken))!.Pin);
        Assert.Equal(CommitSha, gitPin.CommitSha);
        Assert.Null(gitPin.SourceDescriptorSha256);
        Assert.Null(gitPin.SourceObjects);

        // The storage shape, with its publication provenance and no commit.
        var storageJob = await NewJobAsync("2.1.0", null, cancellationToken);
        await ExecuteAsync(
            """
            UPDATE report_service.template_references
               SET resolved_version = '2.1.0', source_descriptor_sha256 = @SourceDescriptorSha256,
                   package_sha256 = @PackageSha256, source_objects = CAST(@ObjectsJson AS jsonb),
                   engine = 'SCRIBAN', engine_version = '7.5.1', schema_ref = 'schema.json', resolved_at = @Now
             WHERE report_job_id = @ReportJobId
            """,
            new
            {
                storage.SourceDescriptorSha256,
                PackageSha256,
                ObjectsJson = objectsJson,
                Now = Now.UtcDateTime,
                ReportJobId = storageJob,
            },
            cancellationToken);
        var storagePin = Assert.IsType<TemplatePin>((await jobs.FindTemplateReferenceAsync(storageJob, cancellationToken))!.Pin);
        Assert.Null(storagePin.CommitSha);
        Assert.Equal(storage.SourceDescriptorSha256, storagePin.SourceDescriptorSha256);
        Assert.Equal(storage.SourceObjects, storagePin.SourceObjects);

        // Both sources at once.
        var mixedJob = await NewJobAsync("2.1.0", null, cancellationToken);
        var mixed = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            """
            UPDATE report_service.template_references
               SET resolved_version = '2.1.0', commit_sha = @CommitSha,
                   source_descriptor_sha256 = @SourceDescriptorSha256, package_sha256 = @PackageSha256,
                   source_objects = CAST(@ObjectsJson AS jsonb),
                   engine = 'SCRIBAN', engine_version = '7.5.1', schema_ref = 'schema.json', resolved_at = @Now
             WHERE report_job_id = @ReportJobId
            """,
            new { CommitSha, storage.SourceDescriptorSha256, PackageSha256, ObjectsJson = objectsJson, Now = Now.UtcDateTime, ReportJobId = mixedJob },
            cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, mixed.SqlState);
        Assert.Equal("template_references_pin_shapes", mixed.ConstraintName);
        Assert.Null((await jobs.FindTemplateReferenceAsync(mixedJob, cancellationToken))!.Pin);

        // A storage pin without its object list.
        var noListJob = await NewJobAsync("2.1.0", null, cancellationToken);
        var noList = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            """
            UPDATE report_service.template_references
               SET resolved_version = '2.1.0', source_descriptor_sha256 = @SourceDescriptorSha256,
                   package_sha256 = @PackageSha256,
                   engine = 'SCRIBAN', engine_version = '7.5.1', schema_ref = 'schema.json', resolved_at = @Now
             WHERE report_job_id = @ReportJobId
            """,
            new { storage.SourceDescriptorSha256, PackageSha256, Now = Now.UtcDateTime, ReportJobId = noListJob },
            cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, noList.SqlState);
        Assert.Equal("template_references_pin_shapes", noList.ConstraintName);

        // A Git pin without its commit.
        var noCommitJob = await NewJobAsync("1.0.0", null, cancellationToken);
        var noCommit = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            """
            UPDATE report_service.template_references
               SET resolved_version = '1.0.0', package_sha256 = @PackageSha256,
                   engine = 'SCRIBAN', engine_version = '7.5.1', schema_ref = 'schema.json', resolved_at = @Now
             WHERE report_job_id = @ReportJobId
            """,
            new { PackageSha256, Now = Now.UtcDateTime, ReportJobId = noCommitJob },
            cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, noCommit.SqlState);
        Assert.Equal("template_references_pin_shapes", noCommit.ConstraintName);
    }

    [Fact]
    public async Task TheGuardKeepsTheNewPinColumnsSetOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var jobs = new PostgresJobStore(_dataSource!);
        var store = new PostgresTemplateVersionStore(_dataSource!);
        var storage = TemplateVersionRecords.StorageSample("pilot-grouped", "2.2.0", owner: "dev-consumer");
        Assert.True((await store.TryInsertAsync(storage, cancellationToken)).WasInserted);
        var job = await NewJobAsync("2.2.0", null, cancellationToken);
        await ExecuteAsync(
            """
            UPDATE report_service.template_references
               SET resolved_version = '2.2.0', source_descriptor_sha256 = @SourceDescriptorSha256,
                   package_sha256 = @PackageSha256, source_objects = CAST(@ObjectsJson AS jsonb),
                   engine = 'SCRIBAN', engine_version = '7.5.1', schema_ref = 'schema.json', resolved_at = @Now
             WHERE report_job_id = @ReportJobId
            """,
            new
            {
                storage.SourceDescriptorSha256,
                PackageSha256,
                ObjectsJson = TemplateSourceObjects.Write(storage.SourceObjects!),
                Now = Now.UtcDateTime,
                ReportJobId = job,
            },
            cancellationToken);
        var pinned = Assert.IsType<TemplatePin>((await jobs.FindTemplateReferenceAsync(job, cancellationToken))!.Pin);

        var descriptor = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE report_service.template_references SET source_descriptor_sha256 = @Other WHERE report_job_id = @ReportJobId",
            new { Other = new string('d', 64), ReportJobId = job },
            cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, descriptor.SqlState);
        Assert.Contains("set once", descriptor.MessageText, StringComparison.Ordinal);

        var objects = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE report_service.template_references SET source_objects = '[]'::jsonb WHERE report_job_id = @ReportJobId",
            new { ReportJobId = job },
            cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, objects.SqlState);
        Assert.Contains("set once", objects.MessageText, StringComparison.Ordinal);

        var stored = Assert.IsType<TemplatePin>((await jobs.FindTemplateReferenceAsync(job, cancellationToken))!.Pin);
        Assert.Equal(pinned.SourceDescriptorSha256, stored.SourceDescriptorSha256);
        Assert.Equal(pinned.SourceObjects, stored.SourceObjects);
    }

    private async Task<string> NewJobAsync(string? requestedVersion, string? baseline, CancellationToken cancellationToken)
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
            VALUES (@ReportJobId, 'dev-consumer', @UploadId, 'QUEUED', @Now, @Now)
            """,
            new { ReportJobId = jobId, UploadId = uploadId, Now = Now.UtcDateTime },
            cancellationToken);
        await ExecuteAsync(
            """
            INSERT INTO report_service.template_references
                (report_job_id, template_id, reference_form, requested_version, baseline_version)
            VALUES
                (@ReportJobId, 'pilot-grouped', @Form, @RequestedVersion, @Baseline)
            """,
            new
            {
                ReportJobId = jobId,
                Form = requestedVersion is null ? "BY_NAME" : "EXACT",
                RequestedVersion = requestedVersion,
                Baseline = baseline,
            },
            cancellationToken);
        return jobId;
    }

    private Task InsertVersionAsync(
        string version,
        string? commitSha,
        string? sourceTreeSha,
        string? sourceDescriptorSha256,
        string? sourceDescriptorKey,
        string? sourceObjectsJson,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            INSERT INTO report_service.template_versions
                (template_id, version, owner_consumer_id, shared, manifest_version, engine, engine_version,
                 schema_ref, commit_sha, source_tree_sha, package_sha256, package_size, file_count,
                 object_key, manifest, taken_at, source_descriptor_sha256, source_descriptor_key, source_objects)
            VALUES
                ('pilot-grouped', @Version, 'dev-consumer', false, '1.0', 'SCRIBAN', '7.5.1', 'schema.json',
                 @CommitSha, @SourceTreeSha, @PackageSha256, 4096, 4,
                 'templates/dev-consumer/pilot-grouped/' || @Version || '/' || @PackageSha256,
                 CAST('{"manifestVersion":"1.0","id":"pilot-grouped","version":"' || @Version || '"}' AS jsonb),
                 @Now, @SourceDescriptorSha256, @SourceDescriptorKey, CAST(@SourceObjectsJson AS jsonb))
            """,
            new
            {
                Version = version,
                CommitSha = commitSha,
                SourceTreeSha = sourceTreeSha,
                PackageSha256,
                Now = Now.UtcDateTime,
                SourceDescriptorSha256 = sourceDescriptorSha256,
                SourceDescriptorKey = sourceDescriptorKey,
                SourceObjectsJson = sourceObjectsJson,
            },
            cancellationToken);

    private async Task ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    private async Task<VersionRow> VersionAsync(string version, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<VersionRow>(new CommandDefinition(
            """
            SELECT template_id AS "TemplateId", version AS "Version", owner_consumer_id AS "OwnerConsumerId",
                   shared AS "Shared", manifest_version AS "ManifestVersion", engine AS "Engine",
                   engine_version AS "EngineVersion", schema_ref AS "SchemaRef", commit_sha AS "CommitSha",
                   source_tree_sha AS "SourceTreeSha", package_sha256 AS "PackageSha256",
                   package_size AS "PackageSize", file_count AS "FileCount", object_key AS "ObjectKey",
                   manifest::text AS "ManifestJson", taken_at AS "TakenAt", xmin::text AS "Xmin"
              FROM report_service.template_versions
             WHERE template_id = 'pilot-grouped' AND version = @Version
            """,
            new { Version = version },
            cancellationToken: cancellationToken));
    }

    private async Task<ProvenanceRow> ProvenanceAsync(string version, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<ProvenanceRow>(new CommandDefinition(
            """
            SELECT source_descriptor_sha256 AS "SourceDescriptorSha256",
                   source_descriptor_key AS "SourceDescriptorKey",
                   source_objects::text AS "SourceObjectsJson"
              FROM report_service.template_versions
             WHERE template_id = 'pilot-grouped' AND version = @Version
            """,
            new { Version = version },
            cancellationToken: cancellationToken));
    }

    private async Task<ReferenceRow> ReferenceAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<ReferenceRow>(new CommandDefinition(
            """
            SELECT report_job_id AS "ReportJobId", resolved_version AS "ResolvedVersion",
                   commit_sha AS "CommitSha", package_sha256 AS "PackageSha256", engine AS "Engine",
                   engine_version AS "EngineVersion", schema_ref AS "SchemaRef", resolved_at AS "ResolvedAt",
                   xmin::text AS "Xmin"
              FROM report_service.template_references
             WHERE report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    /// <summary>One taken version row, in the columns the earlier product wrote.</summary>
    private sealed record VersionRow
    {
        public string TemplateId { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        public string OwnerConsumerId { get; init; } = string.Empty;

        public bool Shared { get; init; }

        public string ManifestVersion { get; init; } = string.Empty;

        public string Engine { get; init; } = string.Empty;

        public string EngineVersion { get; init; } = string.Empty;

        public string SchemaRef { get; init; } = string.Empty;

        public string CommitSha { get; init; } = string.Empty;

        public string SourceTreeSha { get; init; } = string.Empty;

        public string PackageSha256 { get; init; } = string.Empty;

        public long PackageSize { get; init; }

        public int FileCount { get; init; }

        public string ObjectKey { get; init; } = string.Empty;

        public string ManifestJson { get; init; } = string.Empty;

        public DateTime TakenAt { get; init; }

        public string Xmin { get; init; } = string.Empty;
    }

    /// <summary>The three columns the migration adds to a taken version.</summary>
    private sealed record ProvenanceRow
    {
        public string? SourceDescriptorSha256 { get; init; }

        public string? SourceDescriptorKey { get; init; }

        public string? SourceObjectsJson { get; init; }
    }

    /// <summary>One template reference row, in the pin columns the earlier product wrote.</summary>
    private sealed record ReferenceRow
    {
        public string ReportJobId { get; init; } = string.Empty;

        public string ResolvedVersion { get; init; } = string.Empty;

        public string CommitSha { get; init; } = string.Empty;

        public string PackageSha256 { get; init; } = string.Empty;

        public string Engine { get; init; } = string.Empty;

        public string EngineVersion { get; init; } = string.Empty;

        public string SchemaRef { get; init; } = string.Empty;

        public DateTime ResolvedAt { get; init; }

        public string Xmin { get; init; } = string.Empty;
    }
}
