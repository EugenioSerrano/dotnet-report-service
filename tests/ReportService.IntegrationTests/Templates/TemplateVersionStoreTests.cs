using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using ReportService.Migrations;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;
using ReportService.Testing.Manifests;

namespace ReportService.IntegrationTests.Templates;

/// <summary>
/// The taken-version record against a real PostgreSQL 18.6 with the product's
/// migrations applied: a version is recorded once even when eight takes of it
/// insert at the same moment, and the losers read the winner; the table's own
/// checks refuse a row the product can never write, and its two taken shapes are
/// the only ones it admits; a recorded manifest reads back as the same document
/// through <c>jsonb</c> - every member and every value - while the member order
/// and the spacing of the column's own text are never part of the contract; and
/// the pin's three shapes and its set-once tuple hold on the delivered constraint
/// and trigger.
/// </summary>
public sealed class TemplateVersionStoreTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

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
        var sample = Record("round-trip");

        // The document the take records: the sample's own identity members, a
        // decimal whose digits must survive, a number in exponent form, and a
        // member the typed view does not know.
        var document = new JsonObject
        {
            ["manifestVersion"] = sample.ManifestVersion,
            ["id"] = sample.TemplateId,
            ["version"] = sample.Version,
            ["owner"] = sample.OwnerConsumerId,
            ["shared"] = sample.Shared,
            ["engine"] = TemplateEngineNames.ToManifestName(sample.Engine),
            ["engineVersion"] = sample.EngineVersion,
            ["dataContract"] = new JsonObject
            {
                ["profile"] = "header-items-v1",
                ["version"] = "1.0",
                ["schema"] = sample.SchemaRef,
            },
            ["outputs"] = new JsonObject
            {
                ["pdf"] = new JsonObject
                {
                    ["marginMm"] = new JsonObject { ["top"] = JsonValue.Create(15.50m) },
                    ["pageTotals"] = new JsonObject { ["enabled"] = true, ["scale"] = JsonNode.Parse("1e2") },
                },
            },
            ["authoringNotes"] = new JsonObject { ["source"] = "pilot", ["reviewed"] = true },
        };

        var text = TemplateManifestJson.Write(document);
        var record = sample with
        {
            ManifestJson = text,
            Manifest = TemplateManifestJson.ReadTyped(text),
        };

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
        Assert.True(
            ManifestDocuments.AreTheSameDocument(record.ManifestJson, read.ManifestJson),
            $"the manifest read back is not the document the take recorded.{Environment.NewLine}"
            + $"recorded: {record.ManifestJson}{Environment.NewLine}"
            + $"read: {read.ManifestJson}");
        Assert.Equal(record.Manifest.Outputs?.Pdf?.MarginMm?.Top, read.Manifest.Outputs?.Pdf?.MarginMm?.Top);
        Assert.Equal(record.Manifest.Outputs?.Pdf?.PageTotals?.Scale, read.Manifest.Outputs?.Pdf?.PageTotals?.Scale);
        Assert.Equal(record.TakenAt.ToUniversalTime(), read.TakenAt.ToUniversalTime());
    }

    [Fact]
    public async Task AStorageTakenVersionRoundTripsWithItsPublicationProvenance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new PostgresTemplateVersionStore(_dataSource!);
        var record = TemplateVersionRecords.StorageSample(templateId: "storage-round-trip", version: "1.1.0");

        Assert.True((await store.TryInsertAsync(record, cancellationToken)).WasInserted);

        var read = Assert.IsType<TemplateVersionRecord>(await store.FindAsync(record.TemplateId, record.Version, cancellationToken));
        Assert.Null(read.CommitSha);
        Assert.Null(read.SourceTreeSha);
        Assert.Equal(record.SourceDescriptorSha256, read.SourceDescriptorSha256);
        Assert.Equal(record.SourceDescriptorKey, read.SourceDescriptorKey);
        Assert.Equal(record.SourceObjects, read.SourceObjects);
        Assert.Equal(read.FileCount, read.SourceObjects!.Length);
        Assert.Equal(record.PackageSha256, read.PackageSha256);
    }

    public static TheoryData<string, string, string> RefusedTakenShapes => new()
    {
        // A storage-taken row with one Git column, with a part of the source trio,
        // with neither shape, or with a descriptor hash outside the 64-hex syntax.
        { "storage", "commit_sha = 'a1b2c3d4e5f60718293a4b5c6d7e8f9012345678'", "template_versions_provenance" },
        { "storage", "source_tree_sha = 'b2c3d4e5f60718293a4b5c6d7e8f90123456789a'", "template_versions_provenance" },
        { "storage", "source_descriptor_key = NULL", "template_versions_provenance" },
        { "storage", "source_objects = NULL", "template_versions_provenance" },
        { "storage", "commit_sha = NULL, source_tree_sha = NULL, source_descriptor_sha256 = NULL, source_descriptor_key = NULL, source_objects = NULL", "template_versions_provenance" },
        { "storage", "source_descriptor_sha256 = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'", "template_versions_source_descriptor_sha256" },

        // A Git-taken row with one column of the pair, with neither shape, or with
        // a source column beside its Git pair.
        { "git", "source_tree_sha = NULL", "template_versions_provenance" },
        { "git", "commit_sha = NULL", "template_versions_provenance" },
        { "git", "commit_sha = NULL, source_tree_sha = NULL", "template_versions_provenance" },
        { "git", "source_descriptor_sha256 = 'd0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f', source_descriptor_key = 'template-source/key.json', source_objects = '[]'::jsonb", "template_versions_provenance" },
    };

    [Theory]
    [MemberData(nameof(RefusedTakenShapes))]
    public async Task TheTableRefusesAMixtureOfTheTwoTakenShapes(string shape, string assignment, string constraint)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new PostgresTemplateVersionStore(_dataSource!);
        var templateId = "mixture-" + Guid.NewGuid().ToString("N");
        var record = shape == "git"
            ? Record(templateId)
            : TemplateVersionRecords.StorageSample(templateId: templateId, version: "1.1.0");
        Assert.True((await store.TryInsertAsync(record, cancellationToken)).WasInserted);

        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE report_service.template_versions SET {assignment} WHERE template_id = '{templateId}'",
            cancellationToken: cancellationToken)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
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
    public async Task ThePinsDeliveredShapesAdmitAbsentGitAndStorage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new PostgresTemplateVersionStore(_dataSource!);

        // Absent: the reference as acceptance writes it, no pin column set.
        var gitVersion = Record("pin-absent");
        Assert.True((await store.TryInsertAsync(gitVersion, cancellationToken)).WasInserted);
        var absentJob = await NewJobAsync(gitVersion.TemplateId, gitVersion.Version, cancellationToken);
        Assert.Null(await PinColumnAsync(absentJob, "resolved_version", cancellationToken));
        Assert.Null(await PinColumnAsync(absentJob, "commit_sha", cancellationToken));
        Assert.Null(await PinColumnAsync(absentJob, "source_descriptor_sha256", cancellationToken));
        Assert.Null(await PinColumnAsync(absentJob, "source_objects", cancellationToken));
        Assert.Null(await PinColumnAsync(absentJob, "resolved_at", cancellationToken));

        // Git-taken pin: the commit and the six columns that complete it, no source column.
        var gitJob = await NewJobAsync(gitVersion.TemplateId, gitVersion.Version, cancellationToken);
        await ExecuteAsync(GitPinSql, GitPinParameters(gitJob, gitVersion), cancellationToken);
        Assert.Equal(gitVersion.Version, await PinColumnAsync(gitJob, "resolved_version", cancellationToken));
        Assert.Equal(TemplateVersionRecords.CommitSha, await PinColumnAsync(gitJob, "commit_sha", cancellationToken));
        Assert.Null(await PinColumnAsync(gitJob, "source_descriptor_sha256", cancellationToken));
        Assert.Null(await PinColumnAsync(gitJob, "source_objects", cancellationToken));
        Assert.NotNull(await PinColumnAsync(gitJob, "resolved_at", cancellationToken));

        // Storage-taken pin: the publication's provenance and no commit.
        var storageVersion = TemplateVersionRecords.StorageSample(templateId: "pin-storage", version: "1.1.0");
        Assert.True((await store.TryInsertAsync(storageVersion, cancellationToken)).WasInserted);
        var storageJob = await NewJobAsync(storageVersion.TemplateId, storageVersion.Version, cancellationToken);
        await ExecuteAsync(StoragePinSql, StoragePinParameters(storageJob, storageVersion), cancellationToken);
        Assert.Equal(storageVersion.Version, await PinColumnAsync(storageJob, "resolved_version", cancellationToken));
        Assert.Null(await PinColumnAsync(storageJob, "commit_sha", cancellationToken));
        Assert.Equal(storageVersion.SourceDescriptorSha256, await PinColumnAsync(storageJob, "source_descriptor_sha256", cancellationToken));
        Assert.NotNull(await PinColumnAsync(storageJob, "source_objects", cancellationToken));
        Assert.NotNull(await PinColumnAsync(storageJob, "resolved_at", cancellationToken));
    }

    [Theory]
    [InlineData("both-sources")]
    [InlineData("storage-without-objects")]
    [InlineData("git-without-commit")]
    public async Task ThePinsDeliveredConstraintRefusesEveryMixture(string mixture)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new PostgresTemplateVersionStore(_dataSource!);
        var storageVersion = TemplateVersionRecords.StorageSample(
            templateId: "pin-mixed-" + Guid.NewGuid().ToString("N"),
            version: "1.1.0");
        Assert.True((await store.TryInsertAsync(storageVersion, cancellationToken)).WasInserted);
        var job = await NewJobAsync(storageVersion.TemplateId, storageVersion.Version, cancellationToken);

        var (sql, parameters) = mixture switch
        {
            "both-sources" => (BothSourcesPinSql, BothSourcesPinParameters(job, storageVersion)),
            "storage-without-objects" => (StorageWithoutObjectsPinSql, StorageWithoutObjectsPinParameters(job, storageVersion)),
            "git-without-commit" => (GitWithoutCommitPinSql, GitWithoutCommitPinParameters(job, storageVersion)),
            _ => throw new InvalidOperationException($"the case {mixture} has no statement"),
        };

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql, parameters, cancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("template_references_pin_shapes", exception.ConstraintName);
        Assert.Null(await PinColumnAsync(job, "resolved_at", cancellationToken));
    }

    [Fact]
    public async Task ThePinsDeliveredGuardKeepsThePublicationProvenanceSetOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new PostgresTemplateVersionStore(_dataSource!);
        var storageVersion = TemplateVersionRecords.StorageSample(templateId: "pin-guard", version: "1.1.0");
        Assert.True((await store.TryInsertAsync(storageVersion, cancellationToken)).WasInserted);
        var job = await NewJobAsync(storageVersion.TemplateId, storageVersion.Version, cancellationToken);
        await ExecuteAsync(StoragePinSql, StoragePinParameters(job, storageVersion), cancellationToken);
        var pinnedDescriptor = await PinColumnAsync(job, "source_descriptor_sha256", cancellationToken);
        var pinnedObjects = await PinColumnAsync(job, "source_objects", cancellationToken);

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

        Assert.Equal(pinnedDescriptor, await PinColumnAsync(job, "source_descriptor_sha256", cancellationToken));
        Assert.Equal(pinnedObjects, await PinColumnAsync(job, "source_objects", cancellationToken));
    }

    private const string GitPinSql =
        """
        UPDATE report_service.template_references
           SET resolved_version = @Version, commit_sha = @CommitSha, package_sha256 = @PackageSha256,
               engine = @Engine, engine_version = @EngineVersion, schema_ref = @SchemaRef, resolved_at = @Now
         WHERE report_job_id = @ReportJobId
        """;

    private const string StoragePinSql =
        """
        UPDATE report_service.template_references
           SET resolved_version = @Version, source_descriptor_sha256 = @SourceDescriptorSha256,
               package_sha256 = @PackageSha256, source_objects = CAST(@ObjectsJson AS jsonb),
               engine = @Engine, engine_version = @EngineVersion, schema_ref = @SchemaRef, resolved_at = @Now
         WHERE report_job_id = @ReportJobId
        """;

    private const string BothSourcesPinSql =
        """
        UPDATE report_service.template_references
           SET resolved_version = @Version, commit_sha = @CommitSha,
               source_descriptor_sha256 = @SourceDescriptorSha256, package_sha256 = @PackageSha256,
               source_objects = CAST(@ObjectsJson AS jsonb),
               engine = @Engine, engine_version = @EngineVersion, schema_ref = @SchemaRef, resolved_at = @Now
         WHERE report_job_id = @ReportJobId
        """;

    private const string StorageWithoutObjectsPinSql =
        """
        UPDATE report_service.template_references
           SET resolved_version = @Version, source_descriptor_sha256 = @SourceDescriptorSha256,
               package_sha256 = @PackageSha256,
               engine = @Engine, engine_version = @EngineVersion, schema_ref = @SchemaRef, resolved_at = @Now
         WHERE report_job_id = @ReportJobId
        """;

    private const string GitWithoutCommitPinSql =
        """
        UPDATE report_service.template_references
           SET resolved_version = @Version, package_sha256 = @PackageSha256,
               engine = @Engine, engine_version = @EngineVersion, schema_ref = @SchemaRef, resolved_at = @Now
         WHERE report_job_id = @ReportJobId
        """;

    private static object GitPinParameters(string reportJobId, TemplateVersionRecord version) => new
    {
        ReportJobId = reportJobId,
        Version = version.Version,
        CommitSha = TemplateVersionRecords.CommitSha,
        PackageSha256 = version.PackageSha256,
        Engine = TemplateEngineNames.ToStoredText(TemplateEngine.Scriban),
        EngineVersion = version.EngineVersion,
        SchemaRef = version.SchemaRef,
        Now = Now.UtcDateTime,
    };

    private static object StoragePinParameters(string reportJobId, TemplateVersionRecord version) => new
    {
        ReportJobId = reportJobId,
        Version = version.Version,
        SourceDescriptorSha256 = version.SourceDescriptorSha256,
        PackageSha256 = version.PackageSha256,
        ObjectsJson = TemplateSourceObjects.Write(version.SourceObjects!),
        Engine = TemplateEngineNames.ToStoredText(TemplateEngine.Scriban),
        EngineVersion = version.EngineVersion,
        SchemaRef = version.SchemaRef,
        Now = Now.UtcDateTime,
    };

    private static object BothSourcesPinParameters(string reportJobId, TemplateVersionRecord version) => new
    {
        ReportJobId = reportJobId,
        Version = version.Version,
        CommitSha = TemplateVersionRecords.CommitSha,
        SourceDescriptorSha256 = version.SourceDescriptorSha256,
        PackageSha256 = version.PackageSha256,
        ObjectsJson = TemplateSourceObjects.Write(version.SourceObjects!),
        Engine = TemplateEngineNames.ToStoredText(TemplateEngine.Scriban),
        EngineVersion = version.EngineVersion,
        SchemaRef = version.SchemaRef,
        Now = Now.UtcDateTime,
    };

    private static object StorageWithoutObjectsPinParameters(string reportJobId, TemplateVersionRecord version) => new
    {
        ReportJobId = reportJobId,
        Version = version.Version,
        SourceDescriptorSha256 = version.SourceDescriptorSha256,
        PackageSha256 = version.PackageSha256,
        Engine = TemplateEngineNames.ToStoredText(TemplateEngine.Scriban),
        EngineVersion = version.EngineVersion,
        SchemaRef = version.SchemaRef,
        Now = Now.UtcDateTime,
    };

    private static object GitWithoutCommitPinParameters(string reportJobId, TemplateVersionRecord version) => new
    {
        ReportJobId = reportJobId,
        Version = version.Version,
        PackageSha256 = version.PackageSha256,
        Engine = TemplateEngineNames.ToStoredText(TemplateEngine.Scriban),
        EngineVersion = version.EngineVersion,
        SchemaRef = version.SchemaRef,
        Now = Now.UtcDateTime,
    };

    private static async Task<TemplateVersionInsert> InsertAsync(
        PostgresTemplateVersionStore store,
        TemplateVersionRecord record,
        CancellationToken cancellationToken) =>
        await store.TryInsertAsync(record, cancellationToken);

    private static TemplateVersionRecord Record(string templateId) =>
        TemplateVersionRecords.Sample(templateId, "1.1.0", engineVersion: "7.5.1");

    /// <summary>Seeds one accepted job over a ready upload and its exact template reference.</summary>
    /// <param name="templateId">The template the reference names.</param>
    /// <param name="version">The version the reference asks for.</param>
    /// <param name="cancellationToken">The case's token.</param>
    private async Task<string> NewJobAsync(string templateId, string version, CancellationToken cancellationToken)
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
            VALUES (@ReportJobId, @TemplateId, 'EXACT', @Version, NULL)
            """,
            new { ReportJobId = jobId, TemplateId = templateId, Version = version },
            cancellationToken);
        return jobId;
    }

    private async Task<object?> PinColumnAsync(string reportJobId, string column, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync(new CommandDefinition(
            $"SELECT {column} FROM report_service.template_references WHERE report_job_id = @ReportJobId",
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    private async Task<long> ScalarAsync(CancellationToken cancellationToken, string sql)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }
}
