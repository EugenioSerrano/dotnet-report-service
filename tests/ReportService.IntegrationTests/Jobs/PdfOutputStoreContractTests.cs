using Dapper;
using Npgsql;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The publication of a PDF output against a real PostgreSQL 18.6 server: the three tables
/// the printing path writes, the guard that makes a publication and a finalisation one-way,
/// the schema's own refusals, and the code a job ends with when one of its outputs failed.
/// The classes that drive whole jobs through the engine read the same rows; what only a real
/// schema can show is asserted here.
/// </summary>
public sealed class PdfOutputStoreContractTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Consumer = "dev-consumer";

    private static readonly DateTimeOffset Now = new(2026, 11, 5, 3, 15, 0, TimeSpan.Zero);

    private static readonly string Hash = new('c', 64);

    private readonly PostgresFixture _fixture;

    private NpgsqlDataSource? _dataSource;

    private string? _database;

    private PostgresJobStore? _jobs;

    private PostgresUploadStore? _uploads;

    private int _sequence;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public PdfOutputStoreContractTests(PostgresFixture fixture) => _fixture = fixture;

    private IJobStore Jobs => _jobs!;

    private IUploadStore Uploads => _uploads!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _database = await _fixture.CreateDatabaseAsync(cancellationToken);
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(_database))
        {
            Timeout = 5,
            CommandTimeout = PostgresJobStore.CommandTimeoutSeconds,
        }.ConnectionString;
        var migration = await MigrationRunner.RunAsync(connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _jobs = new PostgresJobStore(_dataSource);
        _uploads = new PostgresUploadStore(_dataSource);
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
    public async Task TheSchemaRefusesAnArtifactOutsideTheOutputsNamespaceOrWithoutAPage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAsync(cancellationToken);
        await Jobs.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);

        // A published object is an output-class object, and a page count of zero is not a page.
        var key = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO report_service.artifacts
                (artifact_id, report_job_id, output_id, format, media_type, object_key, sha256, size_bytes, page_count, created_at)
            VALUES (@ArtifactId, @ReportJobId, @OutputId, 'PDF', 'application/pdf', @Key, @Sha256, 2048, 3, @Now)
            """,
            new
            {
                ArtifactId = "artifact-" + new string('a', 32),
                acceptance.ReportJobId,
                acceptance.OutputId,
                Key = $"blocks/{acceptance.ReportJobId}/0/attempt",
                Sha256 = Hash,
                Now,
            },
            cancellationToken: cancellationToken)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, key.SqlState);

        var pages = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO report_service.artifacts
                (artifact_id, report_job_id, output_id, format, media_type, object_key, sha256, size_bytes, page_count, created_at)
            VALUES (@ArtifactId, @ReportJobId, @OutputId, 'PDF', 'application/pdf', @Key, @Sha256, 2048, 0, @Now)
            """,
            new
            {
                ArtifactId = "artifact-" + new string('b', 32),
                acceptance.ReportJobId,
                acceptance.OutputId,
                Key = $"outputs/{acceptance.ReportJobId}/{acceptance.OutputId}/attempt",
                Sha256 = Hash,
                Now,
            },
            cancellationToken: cancellationToken)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, pages.SqlState);
    }

    [Fact]
    public async Task AnOutputThatIsNotRunningIsNeverPublishedAndNothingIsWritten()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAsync(cancellationToken);

        // The output is still pending: the guard refuses the publication, and the artifact
        // table stays empty rather than naming a file no consumer may be told about.
        var refused = await Jobs.PublishAsync(ArtifactOf(acceptance), cancellationToken);

        Assert.Equal(PublishOutcome.GuardRefused, refused.Outcome);
        Assert.Null(refused.ArtifactId);
        Assert.Equal(0L, await CountAsync("artifacts", acceptance.ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task PublicationIsOneWayAndAnswersTheArtifactThatExists()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAsync(cancellationToken);
        await Jobs.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);

        var first = await Jobs.PublishAsync(ArtifactOf(acceptance, "artifact-" + new string('1', 32)), cancellationToken);
        var second = await Jobs.PublishAsync(ArtifactOf(acceptance, "artifact-" + new string('2', 32)), cancellationToken);

        Assert.Equal(PublishOutcome.Published, first.Outcome);
        Assert.Equal(PublishOutcome.AlreadyPublished, second.Outcome);
        Assert.Equal(first.ArtifactId, second.ArtifactId);
        Assert.Equal(1L, await CountAsync("artifacts", acceptance.ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task AFailingOutputEndsTheJobWithItsCodeAndTheReasonOfAnOutputFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAsync(cancellationToken);
        await Jobs.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);

        Assert.Equal(
            OutputFailureOutcome.Applied,
            await Jobs.FailOutputAsync(acceptance.OutputId, "PAGINATION_FAILED", Now, cancellationToken));
        var closed = await Jobs.FinalizeJobAsync(acceptance.ReportJobId, Now.AddSeconds(1), cancellationToken);

        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Failed, closed.Status);
        Assert.Equal("PAGINATION_FAILED", closed.ErrorCode);
        var state = await Jobs.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(ReportJobStatus.Failed, state!.Job.Status);
        Assert.Equal("PAGINATION_FAILED", state.Job.ErrorCode);
        Assert.Equal("output-failed", state.Job.ErrorReason);

        // The closure is one-way: neither a second finalisation nor a later output failure
        // changes the row the first closure wrote.
        Assert.Equal(
            JobFinalizationOutcome.AlreadyTerminal,
            (await Jobs.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(5), cancellationToken)).Outcome);
        Assert.Equal(
            OutputFailureOutcome.AlreadyTerminal,
            await Jobs.FailOutputAsync(acceptance.OutputId, "EXECUTION_FAILED", Now.AddMinutes(5), cancellationToken));
    }

    [Fact]
    public async Task ASucceededOutputClosesTheJobSucceededAndCarriesNoCode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAsync(cancellationToken);
        await Jobs.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        await Jobs.PublishAsync(ArtifactOf(acceptance), cancellationToken);

        var closed = await Jobs.FinalizeJobAsync(acceptance.ReportJobId, Now.AddSeconds(1), cancellationToken);

        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Succeeded, closed.Status);
        Assert.Null(closed.ErrorCode);
        var state = await Jobs.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Null(state!.Job.ErrorCode);
        Assert.Null(state.Job.ErrorReason);
    }

    private ArtifactRecord ArtifactOf(JobAcceptance acceptance, string? artifactId = null) => new(
        artifactId ?? "artifact-" + new string('0', 32),
        acceptance.ReportJobId,
        acceptance.OutputId,
        OutputFormat.Pdf,
        "application/pdf",
        $"outputs/{acceptance.ReportJobId}/{acceptance.OutputId}/attempt",
        Hash,
        2_048,
        3,
        Now);

    private async Task<long> CountAsync(string table, string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT count(*) FROM report_service.{table} WHERE report_job_id = @ReportJobId",
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    // A job accepted over an upload driven to ready through the upload store's own steps.
    private async Task<JobAcceptance> AcceptedAsync(CancellationToken cancellationToken)
    {
        var uploadId = UploadIdentifier.Prefix + (++_sequence).ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
        await Uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                Consumer,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                ObjectKey.FromSegments("staging", uploadId, "content"),
                Now.AddHours(-1),
                Now.AddHours(1)),
            cancellationToken);
        Assert.True(await Uploads.TryStartTransferAsync(
            uploadId, Consumer, UploadContentEncoding.None, Now.AddMinutes(-30), cancellationToken));
        Assert.True(await Uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, 2_048, 2_048, Hash, Now.AddMinutes(-30), cancellationToken));
        Assert.True(await Uploads.ConfirmAsync(
            uploadId, Consumer, null, Now.AddMinutes(-30), Now.AddMinutes(-30), Now.AddHours(5), cancellationToken));
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await Uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    Consumer,
                    ObjectKey.FromSegments("snapshots", uploadId, "a"),
                    UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    Hash,
                    2_048,
                    Hash,
                    2_048,
                    Now.AddMinutes(-20)),
                Now.AddMinutes(-20),
                cancellationToken));

        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            JobIdentifier.CreateOutput(),
            Consumer,
            uploadId,
            "pilot-simple",
            TemplateReferenceForm.Exact,
            "1.0.0");
        Assert.IsType<JobAcceptanceResult.Accepted>(await Jobs.AcceptAsync(acceptance, Now, cancellationToken));
        return acceptance;
    }
}
