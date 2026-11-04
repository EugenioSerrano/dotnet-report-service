using Dapper;
using Npgsql;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.Testing;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The job's template pin on the real database: the same contract the in-memory store is held
/// to, run here on PostgreSQL 18.6 brought to the expected version by the product's own
/// migrations, plus what only the schema can promise — a pin is complete or absent, an exact
/// reference is pinned to exactly the version it named, a by-name pin is a release, a baseline
/// belongs to a by-name reference alone, the pin points at a taken version, the reference as
/// written and a pin already set are immutable, and two attempts racing to pin one job leave
/// the first committed pin and answer it to the loser.
/// </summary>
public sealed class PostgresJobStoreTemplatePinTests : JobStoreContract, IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource? _dataSource;
    private PostgresTemplateVersionStore? _versions;
    private string? _database;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public PostgresJobStoreTemplatePinTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>The taken versions a pin records: the database makes a pin point at one.</summary>
    protected override ITemplateVersionStore? Versions => _versions;

    /// <summary>Creates a database of its own, brings it to the expected version and opens both stores on it.</summary>
    protected override async Task<(IJobStore Jobs, IUploadStore Uploads)> CreateStoresAsync(CancellationToken cancellationToken)
    {
        _database = await _fixture.CreateDatabaseAsync(cancellationToken);
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(_database))
        {
            Timeout = 5,
            CommandTimeout = PostgresJobStore.CommandTimeoutSeconds,
        }.ConnectionString;

        var migration = await MigrationRunner.RunAsync(connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _versions = new PostgresTemplateVersionStore(_dataSource);
        return (new PostgresJobStore(_dataSource), new PostgresUploadStore(_dataSource));
    }

    /// <summary>Releases the data source of the class's stores.</summary>
    protected override async ValueTask DisposeStoresAsync(IJobStore jobs, IUploadStore uploads)
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheSchemaRefusesAPinThatIsNotComplete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var version = await TakenAsync("1.1.0", cancellationToken);
        var reference = await AcceptedReferenceAsync(version: null, baseline: "1.0.0", cancellationToken: cancellationToken);

        // One column of the seven is set: a pin is complete or absent, never half written.
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE report_service.template_references SET resolved_version = @Version WHERE report_job_id = @ReportJobId",
            new { Version = version, reference.ReportJobId },
            cancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("template_references_pin_complete", exception.ConstraintName);
        Assert.Null((await Store.FindTemplateReferenceAsync(reference.ReportJobId, cancellationToken))!.Pin);
    }

    [Fact]
    public async Task TheSchemaRefusesAnExactReferencePinnedToAVersionItDidNotName()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var taken = await TakenAsync("1.1.0", cancellationToken);
        var reference = await AcceptedReferenceAsync(version: "1.0.0", cancellationToken: cancellationToken);

        // An exact reference names the one version it may use: a complete pin of another
        // version is a row the database refuses rather than a job that renders it.
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            PinSql("resolved_version = @Version"),
            PinParameters(reference.ReportJobId, taken),
            cancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("template_references_exact_pin", exception.ConstraintName);
        Assert.Null((await Store.FindTemplateReferenceAsync(reference.ReportJobId, cancellationToken))!.Pin);
    }

    [Fact]
    public async Task TheSchemaRefusesAByNameReferencePinnedToAPreRelease()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var prerelease = await TakenAsync("2.0.0-beta.1", cancellationToken);
        var reference = await AcceptedReferenceAsync(version: null, baseline: "1.0.0", cancellationToken: cancellationToken);

        // A by-name reference resolves to a release, never to a pre-release.
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            PinSql("resolved_version = @Version"),
            PinParameters(reference.ReportJobId, prerelease),
            cancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("template_references_by_name_release", exception.ConstraintName);
    }

    [Fact]
    public async Task TheSchemaRefusesABaselineOnAnExactReference()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reference = await AcceptedReferenceAsync(version: "1.0.0", cancellationToken: cancellationToken);

        // The acceptance writes the reference without a baseline, and the guard makes it
        // immutable, so the row is written again from scratch to reach the schema's own check.
        await ExecuteAsync(
            "DELETE FROM report_service.template_references WHERE report_job_id = @ReportJobId",
            new { reference.ReportJobId },
            cancellationToken);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            """
            INSERT INTO report_service.template_references
                (report_job_id, template_id, reference_form, requested_version, baseline_version)
            VALUES
                (@ReportJobId, 'pilot-grouped', 'EXACT', '1.0.0', '1.0.0')
            """,
            new { reference.ReportJobId },
            cancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("template_references_baseline", exception.ConstraintName);
    }

    [Fact]
    public async Task TheSchemaRefusesAPinThatPointsAtAVersionNobodyTook()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reference = await AcceptedReferenceAsync(version: null, baseline: "1.0.0", cancellationToken: cancellationToken);

        // The pin names the taken version it was copied from; a version no record holds makes
        // the reference point at nothing, which the foreign key refuses.
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            PinSql("resolved_version = @Version"),
            PinParameters(reference.ReportJobId, "9.9.9"),
            cancellationToken));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal("template_references_pin_version", exception.ConstraintName);
    }

    [Fact]
    public async Task TheGuardRefusesAnUpdateOfTheReferenceAsWritten()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reference = await AcceptedReferenceAsync(version: "1.0.0", cancellationToken: cancellationToken);

        // The reference as the consumer wrote it never changes, not even to another template.
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE report_service.template_references SET template_id = 'pilot-grouped-other' WHERE report_job_id = @ReportJobId",
            new { reference.ReportJobId },
            cancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Contains("immutable", exception.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGuardRefusesAChangeOfAPinThatIsAlreadySet()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var version = await TakenAsync("1.0.0", cancellationToken);
        var reference = await AcceptedReferenceAsync(version: "1.0.0", cancellationToken: cancellationToken);
        var pinned = await Store.PinTemplateAsync(reference.ReportJobId, PinOf(version), Now.AddSeconds(1), cancellationToken);
        Assert.True(pinned.WasWrittenByThisCall);

        // A pin is set once: what a job renders is fixed and cannot be rewritten afterwards.
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE report_service.template_references SET commit_sha = @Other WHERE report_job_id = @ReportJobId",
            new { Other = new string('b', 40), reference.ReportJobId },
            cancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Contains("set once", exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(Hash, (await Store.FindTemplateReferenceAsync(reference.ReportJobId, cancellationToken))!.Pin!.PackageSha256);
    }

    [Fact]
    public async Task TwoConcurrentPinsOnOneJobLeaveOnePinAndAnswerTheWinner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var version = await TakenAsync("1.0.0", cancellationToken);
        var reference = await AcceptedReferenceAsync(version: "1.0.0", cancellationToken: cancellationToken);

        // Two attempts reach the row at the same time with different bytes: exactly one of them
        // writes, the other is answered the pin the first committed — never one of its own.
        var first = Store.PinTemplateAsync(reference.ReportJobId, PinOf(version), Now.AddSeconds(1), cancellationToken);
        var second = Store.PinTemplateAsync(
            reference.ReportJobId,
            PinOf(version, hash: new string('b', 64)),
            Now.AddSeconds(2),
            cancellationToken);
        var states = await Task.WhenAll(first, second);

        Assert.Single(states, state => state.WasWrittenByThisCall);
        Assert.All(states, state => Assert.NotNull(state.Pin));
        Assert.All(states, state => Assert.Equal(states[0].Pin!.PackageSha256, state.Pin!.PackageSha256));

        var stored = (await Store.FindTemplateReferenceAsync(reference.ReportJobId, cancellationToken))!.Pin;
        Assert.NotNull(stored);
        Assert.Equal(states[0].Pin!.PackageSha256, stored!.PackageSha256);
        Assert.Equal(1, await CountPinsAsync(reference.ReportJobId, cancellationToken));
    }

    /// <summary>Accepts one job over a ready upload and answers the reference it recorded.</summary>
    private async Task<TemplateReferenceRecord> AcceptedReferenceAsync(
        string? version,
        CancellationToken cancellationToken,
        string? baseline = null)
    {
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId, version: version) with { BaselineVersion = baseline };
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));

        return (await Store.FindTemplateReferenceAsync(acceptance.ReportJobId, cancellationToken))!;
    }

    /// <summary>The statement that writes every pin column, as the store writes them.</summary>
    private static string PinSql(string versionAssignment) =>
        $"""
        UPDATE report_service.template_references
           SET {versionAssignment},
               commit_sha = @CommitSha,
               package_sha256 = @PackageSha256,
               engine = @Engine,
               engine_version = @EngineVersion,
               schema_ref = @SchemaRef,
               resolved_at = now()
         WHERE report_job_id = @ReportJobId
        """;

    /// <summary>The parameters of <see cref="PinSql"/>.</summary>
    private static object PinParameters(string reportJobId, string version) => new
    {
        ReportJobId = reportJobId,
        Version = version,
        CommitSha = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
        PackageSha256 = Hash,
        Engine = TemplateEngineNames.ToStoredText(TemplateEngine.Scriban),
        EngineVersion = "7.5.1",
        SchemaRef = "schema.json",
    };

    private async Task ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    private async Task<long> CountPinsAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM report_service.template_references WHERE report_job_id = @ReportJobId AND resolved_at IS NOT NULL",
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }
}
