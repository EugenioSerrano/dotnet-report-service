using Dapper;
using Npgsql;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.Storage;
using ReportService.Uploads;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The waiting operations of the job store against a real PostgreSQL server: the same
/// contract cases the in-memory store is held to, run here on a database brought to the
/// expected version by the product's own migration set, plus what only a real schema can
/// show — a stage that belongs to another step is never cleared, a job that has ended
/// never begins to wait, the waiting jobs of an upload are read in creation order, and two
/// writes racing one clear leave one legal state rather than a half-written row.
/// </summary>
public sealed class PostgresJobStoreInputWaitTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Consumer = "dev-consumer";
    private const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";

    private static readonly DateTimeOffset Now = new(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource? _dataSource;
    private PostgresJobStore? _jobs;
    private PostgresUploadStore? _uploads;
    private string? _database;
    private int _sequence;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public PostgresJobStoreInputWaitTests(PostgresFixture fixture) => _fixture = fixture;

    private PostgresJobStore Jobs => _jobs!;

    private PostgresUploadStore Uploads => _uploads!;

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
    public async Task AJobThatEndedLaterNeverBeginsToWait()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await AcceptedJobAsync(cancellationToken);
        Assert.Equal(
            JobFailureOutcome.Applied,
            await Jobs.FailJobAsync(reportJobId, "INPUT_NOT_FOUND", "input-expired", Now, cancellationToken));

        Assert.Equal(BeginInputWaitOutcome.NotQueued, await Jobs.BeginInputWaitAsync(reportJobId, cancellationToken));
        Assert.Null((await Jobs.FindForWorkflowAsync(reportJobId, cancellationToken))!.Job.Stage);
    }

    [Fact]
    public async Task AStageThatIsNotTheWaitingStageIsNeverCleared()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await AcceptedJobAsync(cancellationToken);
        Assert.Equal(BeginInputWaitOutcome.Applied, await Jobs.BeginInputWaitAsync(reportJobId, cancellationToken));

        // Only this service writes a stage in production; another step's stage — one the
        // domain enumeration has, which no delivered step writes yet — is put here directly,
        // because the point is what the clear does when it meets one.
        await SetStageAsync(reportJobId, "VALIDATING_DATA", cancellationToken);

        Assert.Equal(EndInputWaitOutcome.NotWaiting, await Jobs.EndInputWaitAsync(reportJobId, cancellationToken));
        Assert.Equal("VALIDATING_DATA", await StoredStageAsync(reportJobId, cancellationToken));
    }

    [Fact]
    public async Task AQueuedJobWithNoStageIsAdmittedAndOneWithAnotherStageIsNot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedUploadAsync(cancellationToken);
        var waiting = await AcceptedJobAsync(cancellationToken, uploadId, Now);
        var betweenSteps = await AcceptedJobAsync(cancellationToken, uploadId, Now.AddSeconds(1));
        var elsewhere = await AcceptedJobAsync(cancellationToken, uploadId, Now.AddSeconds(2));

        Assert.Equal(BeginInputWaitOutcome.Applied, await Jobs.BeginInputWaitAsync(waiting, cancellationToken));
        await SetStageAsync(elsewhere, "VALIDATING_DATA", cancellationToken);

        Assert.Equal(
            [waiting, betweenSteps],
            await Jobs.FindInputWaitingJobIdsAsync(uploadId, cancellationToken));
    }

    [Fact]
    public async Task TwoBeginningsRacingOneClearLeaveOneLegalState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await AcceptedJobAsync(cancellationToken);

        Task[] race =
        [
            Jobs.BeginInputWaitAsync(reportJobId, cancellationToken),
            Jobs.BeginInputWaitAsync(reportJobId, cancellationToken),
            Jobs.EndInputWaitAsync(reportJobId, cancellationToken),
            Jobs.EndInputWaitAsync(reportJobId, cancellationToken),
        ];
        await Task.WhenAll(race);

        var state = await Jobs.FindForWorkflowAsync(reportJobId, cancellationToken);
        Assert.Equal(ReportJobStatus.Queued, state!.Job.Status);
        Assert.Null(state.Job.ErrorCode);
        Assert.True(
            state.Job.Stage is null or InputWaitStage.WaitingForInput,
            $"a racing change left the stage as '{state.Job.Stage}', which no sequence of the two operations writes.");
    }

    [Fact]
    public async Task TheStageIsStoredInTheDomainTextOfThePipeline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await AcceptedJobAsync(cancellationToken);

        await Jobs.BeginInputWaitAsync(reportJobId, cancellationToken);

        Assert.Equal("WAITING_FOR_INPUT", await StoredStageAsync(reportJobId, cancellationToken));
    }

    private async Task<string> AcceptedJobAsync(
        CancellationToken cancellationToken,
        string? uploadId = null,
        DateTimeOffset? acceptedAt = null)
    {
        uploadId ??= await SeedUploadAsync(cancellationToken);
        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            JobIdentifier.CreateOutput(),
            Consumer,
            uploadId,
            "pilot-grouped",
            TemplateReferenceForm.Exact,
            "1.0.0");

        var accepted = await Jobs.AcceptAsync(acceptance, acceptedAt ?? Now, cancellationToken);
        Assert.IsType<JobAcceptanceResult.Accepted>(accepted);
        return acceptance.ReportJobId;
    }

    private async Task<string> SeedUploadAsync(CancellationToken cancellationToken)
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
            uploadId, UploadContentOutcome.Complete, 900, 800, Hash, Now.AddMinutes(-20), cancellationToken));
        Assert.True(await Uploads.ConfirmAsync(
            uploadId, Consumer, Hash, Now.AddMinutes(-20), Now.AddMinutes(-20), Now.AddHours(6), cancellationToken));
        return uploadId;
    }

    private async Task SetStageAsync(string reportJobId, string stage, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE report_service.report_jobs SET stage = @Stage WHERE report_job_id = @ReportJobId",
            new { Stage = stage, ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<string?> StoredStageAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT stage FROM report_service.report_jobs WHERE report_job_id = @ReportJobId",
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }
}
