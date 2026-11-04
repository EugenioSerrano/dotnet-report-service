using Dapper;
using Npgsql;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.Testing;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The job store against a real PostgreSQL server: the same contract the in-memory
/// store is held to, run here on a database brought to the version the code expects by
/// the product's own migration set, plus what only a real schema can show — the checks
/// that refuse a job or a reference that cannot be one, an acceptance that leaves
/// nothing behind when its start request cannot be written, and an upload that ends in
/// another transaction while an acceptance is deciding about it.
/// </summary>
public sealed class PostgresJobStoreContractTests : JobStoreContract, IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource? _dataSource;
    private PostgresTemplateVersionStore? _versions;
    private string? _database;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public PostgresJobStoreContractTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>The taken versions a pinned case records: the database makes a pin point at one.</summary>
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
    public async Task TheSchemaRefusesAJobWhoseIdentifierIsNotOneOfItsOwn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO report_service.report_jobs
                (report_job_id, consumer_id, upload_id, status, report_clock, created_at)
            VALUES
                ('job-not-a-grammar', 'dev-consumer', 'upload-ffffffffffffffffffffffffffffffff', 'QUEUED', now(), now())
            """,
            cancellationToken: cancellationToken)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task TheSchemaRefusesAJobThatEndedWithoutItsTimeOrFailedWithoutItsCode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);

        // Ended without the instant it ended.
        var endedWithoutTime = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO report_service.report_jobs
                (report_job_id, consumer_id, upload_id, status, report_clock, created_at)
            VALUES
                ('job-00000000000000000000000000000001', 'dev-consumer', @UploadId, 'SUCCEEDED', now(), now())
            """,
            new { UploadId = uploadId },
            cancellationToken: cancellationToken)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, endedWithoutTime.SqlState);

        // Failed without the condition it failed with.
        var failedWithoutCode = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO report_service.report_jobs
                (report_job_id, consumer_id, upload_id, status, report_clock, created_at, finished_at)
            VALUES
                ('job-00000000000000000000000000000002', 'dev-consumer', @UploadId, 'FAILED', now(), now(), now())
            """,
            new { UploadId = uploadId },
            cancellationToken: cancellationToken)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failedWithoutCode.SqlState);
    }

    [Fact]
    public async Task TheSchemaRefusesAReferenceWhoseFormAndVersionDisagree()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId);
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);

        // A reference by name that names a version, and an exact one that names none.
        foreach (var (form, version) in new[] { ("BY_NAME", "1.0.0"), ("EXACT", null) })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO report_service.template_references
                    (report_job_id, template_id, reference_form, requested_version)
                VALUES
                    ('job-00000000000000000000000000000003', 'pilot-grouped', @Form, @Version)
                """,
                new { Form = form, Version = version },
                cancellationToken: cancellationToken)));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }
    }

    [Fact]
    public async Task NothingIsStoredWhenTheStartRequestCannotBeWritten()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId);

        // The start request of this very job already exists, so the acceptance's own insert
        // cannot be written: the transaction has to leave the job, its output and its
        // reference unwritten rather than half written.
        await using (var connection = await _dataSource!.OpenConnectionAsync(cancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO report_service.intents (kind, subject_id, status, attempts, created_at, next_attempt_at)
                VALUES ('start-job', @ReportJobId, 'PENDING', 0, @Now, @Now)
                """,
                new { acceptance.ReportJobId, Now },
                cancellationToken: cancellationToken));
        }

        await Assert.ThrowsAsync<PostgresException>(() => Store.AcceptAsync(acceptance, Now, cancellationToken));

        await using var verify = await _dataSource!.OpenConnectionAsync(cancellationToken);
        foreach (var table in new[] { "report_jobs", "report_outputs", "template_references" })
        {
            var rows = await verify.ExecuteScalarAsync<long>(new CommandDefinition(
                $"SELECT count(*) FROM report_service.{table} WHERE report_job_id = @ReportJobId",
                new { acceptance.ReportJobId },
                cancellationToken: cancellationToken));
            Assert.Equal(0, rows);
        }

        Assert.Null(await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task AnUploadRefusedInAnotherTransactionIsRefusedByTheAcceptance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Verifying, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId);

        // Another transaction refuses the upload and stays open: the acceptance's own read has
        // to wait for it, and what it then sees is the refused upload, not the one it started with.
        await using var connection = await _fixture.OpenAsync(_database!, cancellationToken);
        await using var refusal = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE report_service.uploads
               SET status = 'REJECTED', rejection_code = 'DATA_INVALID', rejection_reason = 'truncated'
             WHERE upload_id = @UploadId
            """,
            new { UploadId = uploadId },
            refusal,
            cancellationToken: cancellationToken));

        var deciding = Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Wait.UntilAsync(
            "the acceptance waiting for the upload's row",
            async token =>
            {
                await using var probe = await _fixture.OpenAsync(_database!, token);
                var waiting = await probe.ExecuteScalarAsync<long>(new CommandDefinition(
                    """
                    SELECT count(*)
                      FROM pg_stat_activity
                     WHERE wait_event_type = 'Lock' AND datname = current_database()
                    """,
                    cancellationToken: token));
                return waiting > 0;
            },
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(100),
            cancellationToken);

        await refusal.CommitAsync(cancellationToken);

        Assert.True(await deciding.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken) is JobAcceptanceResult.UploadUnusable);
        Assert.Null(await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task TheSchemaRefusesAStageOutsideItsSetAndACounterPastItsTotal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId);
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);

        // A step the product does not have: the stage column admits the row's own set, so a
        // value outside it is a corrupt row the database refuses rather than an answer.
        var stage = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
            "UPDATE report_service.report_jobs SET stage = 'NOT_A_STEP' WHERE report_job_id = @ReportJobId",
            new { acceptance.ReportJobId },
            cancellationToken: cancellationToken)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, stage.SqlState);

        // More records read than the snapshot is known to hold would be a total the consumer
        // could not trust; the row is refused, so no answer can carry that pair.
        var records = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
            "UPDATE report_service.report_jobs SET records_read = 5, records_total = 4 WHERE report_job_id = @ReportJobId",
            new { acceptance.ReportJobId },
            cancellationToken: cancellationToken)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, records.SqlState);

        // The same pair for the blocks: a job never completes more of them than it has.
        var blocks = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
            "UPDATE report_service.report_jobs SET blocks_completed = 2, blocks_total = 1 WHERE report_job_id = @ReportJobId",
            new { acceptance.ReportJobId },
            cancellationToken: cancellationToken)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, blocks.SqlState);
    }

    [Fact]
    public async Task TwoConcurrentProgressWritesEndAtTheHigherCountWhicheverOrderTheyTake()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId);
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));

        // Two writers of two attempts reach the row at the same time, in either order: the
        // counter ends at the higher count and never at the one that arrived last.
        var lower = Store.RecordRecordsReadAsync(acceptance.ReportJobId, 100, cancellationToken);
        var higher = Store.RecordRecordsReadAsync(acceptance.ReportJobId, 200, cancellationToken);
        await Task.WhenAll(lower, higher);

        Assert.Equal(200, (await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken))!.RecordsRead);
    }

    [Fact]
    public async Task AStatusReadTakenWhileAPublicationCommitsSeesOneStateOrTheOther()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId);
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));
        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);

        // The publication is held between its artifact insert and its status write by a row
        // lock another transaction takes first, so the read happens exactly while the two are
        // written: the pair a consumer sees is one state or the other, never a mix.
        await using var connection = await _fixture.OpenAsync(_database!, cancellationToken);
        await using var holding = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT 1 FROM report_service.report_outputs WHERE output_id = @OutputId FOR UPDATE",
            new { acceptance.OutputId },
            holding,
            cancellationToken: cancellationToken));

        var publishing = Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.OutputId), cancellationToken);
        await Wait.UntilAsync(
            "the publication waiting for the output's row",
            async token =>
            {
                await using var probe = await _fixture.OpenAsync(_database!, token);
                var waiting = await probe.ExecuteScalarAsync<long>(new CommandDefinition(
                    """
                    SELECT count(*)
                      FROM pg_stat_activity
                     WHERE wait_event_type = 'Lock' AND datname = current_database()
                    """,
                    cancellationToken: token));
                return waiting > 0;
            },
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(100),
            cancellationToken);

        var during = Assert.Single(
            (await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken))!.Outputs);
        Assert.Equal(OutputStatus.Running, during.Status);
        Assert.Null(during.ArtifactId);

        await holding.CommitAsync(cancellationToken);
        Assert.True(await publishing.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken) is { Outcome: PublishOutcome.Published });

        var after = Assert.Single(
            (await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken))!.Outputs);
        Assert.Equal(OutputStatus.Succeeded, after.Status);
        Assert.Equal(ArtifactId, after.ArtifactId);
    }

    [Fact]
    public async Task AFinalizationThatRepeatsLeavesTheFirstTerminalWriteExactly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId);
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));
        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        Assert.Equal(PublishOutcome.Published, (await Store.PublishAsync(
            Artifact(acceptance.ReportJobId, acceptance.OutputId),
            cancellationToken)).Outcome);

        Assert.Equal(JobFinalizationOutcome.Applied, (await Store.FinalizeJobAsync(
            acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken)).Outcome);
        var first = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);

        // A retried step that repeats the finalisation writes nothing: the closure the first
        // call wrote stands, down to the instant it ended and the code it carries.
        Assert.Equal(JobFinalizationOutcome.AlreadyTerminal, (await Store.FinalizeJobAsync(
            acceptance.ReportJobId, Now.AddMinutes(5), cancellationToken)).Outcome);
        var after = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);

        Assert.Equal(ReportJobStatus.Succeeded, after!.Status);
        Assert.Equal(first!.FinishedAt, after.FinishedAt);
        Assert.Equal(first.ErrorCode, after.ErrorCode);
        Assert.Equal(first.Status, after.Status);
    }

    [Fact]
    public async Task AJobAcceptedOnAUsableUploadStandsAfterTheUploadEndsLater()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Verifying, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId);

        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));

        // The other side of the same race: the acceptance committed first, so the upload's later
        // end cannot undo the job. What it does do is end the job when a step looks at it again.
        Assert.True(await Uploads.CommitRejectedAsync(
            uploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, Now.AddMinutes(1), cancellationToken));

        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.NotNull(state);
        Assert.Equal(ReportJobStatus.Queued, state!.Job.Status);
        Assert.Equal(UploadStatus.Rejected, state.UploadStatus);
        Assert.Equal(UploadRejectionCode.DataInvalid, state.RejectionCode);
    }
}
