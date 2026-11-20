using ReportService.Testing;
using Dapper;
using Npgsql;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.Storage;
using ReportService.Uploads;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The presentation a job carries, against a real PostgreSQL server brought to the
/// expected version by the product's own migration set: an acceptance stores the two
/// values it was given, the job read and the step's read of the reference answer them
/// unchanged, and the schema refuses to change a value that is already set. A value
/// the request could not carry — one over the bound the request enforces — cannot come
/// in by another path either.
/// </summary>
public sealed class PostgresJobStoreLocaleTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Consumer = "dev-consumer";
    private const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";

    private static readonly DateTimeOffset Now = new(2026, 11, 2, 7, 30, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource? _dataSource;
    private PostgresJobStore? _jobs;
    private PostgresUploadStore? _uploads;
    private string? _database;
    private int _sequence;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public PostgresJobStoreLocaleTests(PostgresFixture fixture) => _fixture = fixture;

    private PostgresJobStore Jobs => _jobs!;

    private NpgsqlDataSource DataSource => _dataSource!;

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
    public async Task AJobIsAcceptedWithThePresentationItWasGivenAndBothReadsAnswerIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var accepted = await AcceptedJobAsync("es-AR", "America/Argentina/Buenos_Aires", cancellationToken);

        var state = await Jobs.FindForWorkflowAsync(accepted, cancellationToken);
        Assert.Equal("es-AR", state!.Job.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", state.Job.TimeZone);

        var reference = await Jobs.FindTemplateReferenceAsync(accepted, cancellationToken);
        Assert.Equal("es-AR", reference!.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", reference.TimeZone);
        Assert.Null(reference.Pin);
    }

    [Fact]
    public async Task AJobAcceptedWithNoPresentationCarriesNone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var accepted = await AcceptedJobAsync(null, null, cancellationToken);

        var state = await Jobs.FindForWorkflowAsync(accepted, cancellationToken);
        Assert.Null(state!.Job.Culture);
        Assert.Null(state.Job.TimeZone);

        var reference = await Jobs.FindTemplateReferenceAsync(accepted, cancellationToken);
        Assert.Null(reference!.Culture);
        Assert.Null(reference.TimeZone);
    }

    [Fact]
    public async Task TheSchemaRefusesAChangeOfACultureAlreadySet()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var accepted = await AcceptedJobAsync("es-AR", null, cancellationToken);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            UpdateLocaleAsync(accepted, "en-GB", null, cancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Contains("the job culture is set once", exception.MessageText, StringComparison.Ordinal);
        Assert.Equal("es-AR", (await Jobs.FindForWorkflowAsync(accepted, cancellationToken))!.Job.Culture);
    }

    [Fact]
    public async Task TheSchemaRefusesAChangeOfATimeZoneAlreadySet()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var accepted = await AcceptedJobAsync(null, "Europe/Madrid", cancellationToken);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            UpdateLocaleAsync(accepted, null, "UTC", cancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Contains("the job time zone is set once", exception.MessageText, StringComparison.Ordinal);
        Assert.Equal("Europe/Madrid", (await Jobs.FindForWorkflowAsync(accepted, cancellationToken))!.Job.TimeZone);
    }

    [Fact]
    public async Task TheSchemaAdmitsWritingTheSameValueAgainAndCompletingAMissingOneOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var accepted = await AcceptedJobAsync("es-AR", null, cancellationToken);

        // The same value again is a write, not a change: a retry of the step that wrote it
        // must not fail against the guard.
        await UpdateLocaleAsync(accepted, "es-AR", null, cancellationToken);

        // The value the request left out is completed once, by the write that fixes the
        // template; a second attempt to give it another value is refused.
        await UpdateLocaleAsync(accepted, null, "Europe/Madrid", cancellationToken);
        Assert.Equal("Europe/Madrid", (await Jobs.FindForWorkflowAsync(accepted, cancellationToken))!.Job.TimeZone);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            UpdateLocaleAsync(accepted, null, "UTC", cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("Europe/Madrid", (await Jobs.FindForWorkflowAsync(accepted, cancellationToken))!.Job.TimeZone);
    }

    [Fact]
    public async Task AValueOfTheLongestLengthIsStoredAndOneOverTheBoundIsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var longest = new string('c', 64);
        var accepted = await AcceptedJobAsync(longest, null, cancellationToken);

        Assert.Equal(longest, (await Jobs.FindForWorkflowAsync(accepted, cancellationToken))!.Job.Culture);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            UpdateLocaleAsync(accepted, null, new string('z', 65), cancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        var state = await Jobs.FindForWorkflowAsync(accepted, cancellationToken);
        Assert.Equal(longest, state!.Job.Culture);
        Assert.Null(state.Job.TimeZone);
    }

    private async Task<string> AcceptedJobAsync(string? culture, string? timeZone, CancellationToken cancellationToken)
    {
        var uploadId = await SeedUploadAsync(cancellationToken);
        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            JobIdentifier.CreateOutput(),
            Consumer,
            uploadId,
            "pilot-grouped",
            TemplateReferenceForm.Exact,
            "1.0.0",
            AcceptanceIdentity.For(uploadId))
        {
            Culture = culture,
            TimeZone = timeZone,
        };

        var accepted = await Jobs.AcceptAsync(acceptance, Now, cancellationToken);
        Assert.IsType<JobAcceptanceResult.Accepted>(accepted);
        return acceptance.ReportJobId;
    }

    // The one write a path other than an acceptance may make: the completion of a value the
    // request left out, or a repeated write of the value already there.
    private async Task UpdateLocaleAsync(
        string reportJobId,
        string? culture,
        string? timeZone,
        CancellationToken cancellationToken)
    {
        await using var connection = await DataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE report_service.report_jobs
               SET culture   = COALESCE(@Culture, culture),
                   time_zone = COALESCE(@TimeZone, time_zone)
             WHERE report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId, Culture = culture, TimeZone = timeZone },
            cancellationToken: cancellationToken));
    }

    private async Task<string> SeedUploadAsync(CancellationToken cancellationToken)
    {
        var uploadId = UploadIdentifier.Prefix + (++_sequence).ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
        await _uploads!.CreateAsync(
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
        Assert.True(await _uploads.TryStartTransferAsync(
            uploadId, Consumer, UploadContentEncoding.None, Now.AddMinutes(-30), cancellationToken));
        Assert.True(await _uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, 900, 800, Hash, Now.AddMinutes(-20), cancellationToken));
        Assert.True(await _uploads.ConfirmAsync(
            uploadId, Consumer, Hash, Now.AddMinutes(-20), Now.AddMinutes(-20), Now.AddHours(6), cancellationToken));
        return uploadId;
    }
}
