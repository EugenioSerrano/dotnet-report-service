using System.Globalization;
using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Uploads;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// A job whose PDF was published, read back by its owner: the state, the artifact's
/// identifier and the page the artifact counts, the empty report whose total is a known
/// zero, and a job sampled at the step that renders â€” against real PostgreSQL and the real
/// HTTP pipeline.
/// <para>
/// This class is kept out of a run that cannot print by its trait, and it runs in the
/// covering run of the commit that carries the steps that render, verify and publish. On the
/// prepared tree those steps are not part of the build, so the published state is written
/// through the store's own writers â€” the very calls the steps make â€” and the read is proved
/// against that state; the covering run reads the state the steps produced themselves.
/// </para>
/// </summary>
[Trait("Requires", "printing")]
public sealed class JobStatusPublishedTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    /// <summary>The consumer the class's jobs belong to.</summary>
    private const string Consumer = "consumer-a";

    /// <summary>The template every job of this class is accepted for.</summary>
    private const string TemplateId = "pilot-simple";

    /// <summary>The version every job of this class names.</summary>
    private const string Version = "1.0.0";

    /// <summary>A hash of the case's own: the stored bytes are never read by this class.</summary>
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>The identifier every artifact this class publishes carries.</summary>
    private const string ArtifactId = "artifact-0123456789abcdef0123456789abcdef";

    private readonly PostgresFixture _fixture;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-job-status-published",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private string? _database;

    private NpgsqlDataSource? _dataSource;

    private WebApplicationFactory<Program>? _api;

    /// <summary>Runs against the class's PostgreSQL server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public JobStatusPublishedTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        _database = await _fixture.CreateDatabaseAsync(cancellationToken);
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(_database))
        {
            Timeout = 5,
            CommandTimeout = PostgresJobStore.CommandTimeoutSeconds,
        }.ConnectionString;

        var migration = await MigrationRunner.RunAsync(connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(connectionString);

        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(
            registry,
            """
            { "consumers": [
                { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true },
                { "consumerId": "consumer-b", "displayName": "Consumer B", "active": true } ] }
            """);
        _api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory, "storage"));
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
        });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    [Fact]
    public async Task APublishedJobIsReadAsSucceededWithItsArtifactAndThePagesTheArtifactCounts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (job, store) = await AcceptedJobAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        // The steps a published job went through, written through the store's own writers in
        // the order the product writes them: the partition, the first transition with its
        // stage, the rendered block's commit, the publication and the closure from the outputs.
        Assert.Equal(PartitionCommitOutcome.Committed, (await store.CommitPartitionAsync(
            Partition(job.ReportJobId, recordCount: 1_000, blockCount: 1), cancellationToken)).Outcome);
        Assert.Equal(TransitionOutcome.Applied, (await store.TransitionJobAsync(
            job.ReportJobId, [ReportJobStatus.Validating], ReportJobStatus.Running, now, cancellationToken)).Outcome);
        Assert.Equal(TransitionOutcome.Applied, (await store.TransitionOutputAsync(job.OutputId, now, cancellationToken)));
        Assert.Equal(PartCommitOutcome.Committed, (await store.CommitPartAsync(
            new PartCommit(job.OutputId, 0, $"block/{job.ReportJobId}/0/attempt", Hash, 20_480, 3, Hash, now),
            cancellationToken)).Outcome);
        Assert.Equal(PublishOutcome.Published, (await store.PublishAsync(
            new ArtifactRecord(
                ArtifactId,
                job.ReportJobId,
                job.OutputId,
                OutputFormat.Pdf,
                "application/pdf",
                $"outputs/{job.ReportJobId}/{job.OutputId}/attempt",
                Hash,
                65_536,
                3,
                now),
            cancellationToken)).Outcome);
        Assert.Equal(JobFinalizationOutcome.Applied, (await store.FinalizeJobAsync(
            job.ReportJobId, now.AddSeconds(1), cancellationToken)).Outcome);

        using var client = _api!.CreateClient();
        var answer = await ReadAsync(client, job.ReportJobId, cancellationToken);
        Assert.Equal("succeeded", answer.Status);
        Assert.Null(answer.Stage);
        Assert.Null(answer.ErrorCode);
        Assert.Equal(1_000, answer.RecordsRead);
        Assert.Equal(1_000, answer.RecordsTotal);
        Assert.Equal(1, answer.BlocksTotal);
        Assert.Equal(1, answer.BlocksCompleted);
        Assert.Equal(3, answer.PagesGenerated);
        Assert.NotNull(answer.FinishedAt);

        var output = Assert.Single(answer.Outputs);
        Assert.Equal("pdf", output.Format);
        Assert.Equal("succeeded", output.Status);
        Assert.Null(output.ErrorCode);
        Assert.Equal(await ArtifactIdAsync(job.ReportJobId, cancellationToken), output.ArtifactId);
        Assert.Equal(await ArtifactPageCountAsync(job.ReportJobId, cancellationToken), (int?)answer.PagesGenerated);
    }

    [Fact]
    public async Task AJobOverAZeroRecordSnapshotIsReadAsSucceededWithAKnownEmptyTotal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (job, store) = await AcceptedJobAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        // The empty snapshot commits one block holding no record: its total is a known zero,
        // never an unknown one, and one page is still published.
        Assert.Equal(PartitionCommitOutcome.Committed, (await store.CommitPartitionAsync(
            Partition(job.ReportJobId, recordCount: 0, blockCount: 1), cancellationToken)).Outcome);
        Assert.Equal(TransitionOutcome.Applied, (await store.TransitionJobAsync(
            job.ReportJobId, [ReportJobStatus.Validating], ReportJobStatus.Running, now, cancellationToken)).Outcome);
        Assert.Equal(TransitionOutcome.Applied, (await store.TransitionOutputAsync(job.OutputId, now, cancellationToken)));
        Assert.Equal(PartCommitOutcome.Committed, (await store.CommitPartAsync(
            new PartCommit(job.OutputId, 0, $"block/{job.ReportJobId}/0/attempt", Hash, 4_096, 1, Hash, now),
            cancellationToken)).Outcome);
        Assert.Equal(PublishOutcome.Published, (await store.PublishAsync(
            new ArtifactRecord(
                ArtifactId,
                job.ReportJobId,
                job.OutputId,
                OutputFormat.Pdf,
                "application/pdf",
                $"outputs/{job.ReportJobId}/{job.OutputId}/attempt",
                Hash,
                8_192,
                1,
                now),
            cancellationToken)).Outcome);
        Assert.Equal(JobFinalizationOutcome.Applied, (await store.FinalizeJobAsync(
            job.ReportJobId, now.AddSeconds(1), cancellationToken)).Outcome);

        using var client = _api!.CreateClient();
        var answer = await ReadAsync(client, job.ReportJobId, cancellationToken);
        Assert.Equal("succeeded", answer.Status);
        Assert.Null(answer.Stage);
        Assert.Equal(0, answer.RecordsRead);
        Assert.Equal(0, answer.RecordsTotal);
        Assert.Equal(1, answer.BlocksTotal);
        Assert.Equal(1, answer.BlocksCompleted);
        Assert.True(answer.PagesGenerated >= 1, $"an empty report still has one page, and this one counted {answer.PagesGenerated}");
        Assert.Null(answer.ErrorCode);

        var output = Assert.Single(answer.Outputs);
        Assert.Equal("succeeded", output.Status);
        Assert.Equal(await ArtifactIdAsync(job.ReportJobId, cancellationToken), output.ArtifactId);
    }

    [Fact]
    public async Task AJobSampledAtTheRenderStepShowsItsStageWithItsTotalsKnown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (job, store) = await AcceptedJobAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(PartitionCommitOutcome.Committed, (await store.CommitPartitionAsync(
            Partition(job.ReportJobId, recordCount: 1_000, blockCount: 1), cancellationToken)).Outcome);

        // The printing delivery's transition: the job enters the step that renders its PDF, and
        // the read shows the step and the totals the partition already fixed.
        Assert.Equal(TransitionOutcome.Applied, (await store.TransitionJobAsync(
            job.ReportJobId, [ReportJobStatus.Validating], ReportJobStatus.Running, now, cancellationToken)).Outcome);

        using var client = _api!.CreateClient();
        var during = await ReadAsync(client, job.ReportJobId, cancellationToken);
        Assert.Equal("running", during.Status);
        Assert.Equal("rendering-pdf", during.Stage);
        Assert.Equal(1_000, during.RecordsTotal);
        Assert.Equal(1, during.BlocksTotal);
        Assert.Equal(0, during.BlocksCompleted);
        Assert.Equal(0, during.PagesGenerated);
        var output = Assert.Single(during.Outputs);
        Assert.Equal("pending", output.Status);
        Assert.Null(output.ArtifactId);
    }

    private static async Task<Answer> ReadAsync(HttpClient client, string reportJobId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "every status answer has to be uncacheable");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var progress = root.GetProperty("progress");
        return new Answer(
            root.GetProperty("status").GetString()!,
            root.GetProperty("stage") is { ValueKind: JsonValueKind.Null } ? null : root.GetProperty("stage").GetString(),
            progress.GetProperty("recordsRead").GetInt64(),
            progress.GetProperty("recordsTotal") is { ValueKind: JsonValueKind.Null } ? null : progress.GetProperty("recordsTotal").GetInt64(),
            progress.GetProperty("blocksCompleted").GetInt32(),
            progress.GetProperty("blocksTotal") is { ValueKind: JsonValueKind.Null } ? null : progress.GetProperty("blocksTotal").GetInt32(),
            progress.GetProperty("pagesGenerated").GetInt64(),
            root.GetProperty("errorCode") is { ValueKind: JsonValueKind.Null } ? null : root.GetProperty("errorCode").GetString(),
            root.GetProperty("finishedAt") is { ValueKind: JsonValueKind.Null } ? null : root.GetProperty("finishedAt").GetString(),
            [
                .. root.GetProperty("outputs").EnumerateArray().Select(element => new OutputAnswer(
                    element.GetProperty("format").GetString()!,
                    element.GetProperty("status").GetString()!,
                    element.GetProperty("artifactId") is { ValueKind: JsonValueKind.Null } ? null : element.GetProperty("artifactId").GetString(),
                    element.GetProperty("errorCode") is { ValueKind: JsonValueKind.Null } ? null : element.GetProperty("errorCode").GetString())),
            ]);
    }

    // A job accepted for this class's template, on a ready upload of its own: the state the
    // accepted job starts in, with no partition and no output started.
    private async Task<(JobAccepted Job, IJobStore Store)> AcceptedJobAsync(CancellationToken cancellationToken)
    {
        var uploads = new PostgresUploadStore(_dataSource!);
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        var now = DateTimeOffset.UtcNow;
        var record = new UploadRecord(
            uploadId,
            Consumer,
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Created,
            ObjectKey.FromSegments("staging", uploadId, "content"),
            now,
            now.AddHours(1));
        await uploads.CreateAsync(record, cancellationToken);
        Assert.True(await uploads.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.None, now, cancellationToken));
        Assert.True(await uploads.RecordTransferAsync(uploadId, UploadContentOutcome.Complete, 900, 800, Hash, now, cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, Consumer, Hash, now, now, now.AddHours(5), cancellationToken));
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    Consumer,
                    ObjectKey.FromSegments("snapshots", uploadId, "content"),
                    UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    Hash,
                    900,
                    Hash,
                    900,
                    now),
                now,
                cancellationToken));

        var store = new PostgresJobStore(_dataSource!);
        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            JobIdentifier.CreateOutput(),
            Consumer,
            uploadId,
            TemplateId,
            TemplateReferenceForm.Exact,
            Version);
        Assert.IsType<JobAcceptanceResult.Accepted>(await store.AcceptAsync(acceptance, now, cancellationToken));

        // The first transition is the step the run takes out of queued, and it carries the stage.
        Assert.Equal(TransitionOutcome.Applied, (await store.TransitionJobAsync(
            acceptance.ReportJobId, [ReportJobStatus.Queued], ReportJobStatus.Validating, now, cancellationToken)).Outcome);
        return (new JobAccepted(acceptance.ReportJobId, acceptance.OutputId), store);
    }

    private static PartitionRecord Partition(string reportJobId, long recordCount, int blockCount) => new(
        reportJobId,
        1,
        blockCount,
        recordCount,
        24,
        $"index/{reportJobId}/0/attempt",
        Hash,
        120,
        DateTimeOffset.UtcNow);

    private async Task<string?> ArtifactIdAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT artifact_id FROM report_service.artifacts WHERE report_job_id = @ReportJobId",
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<int?> ArtifactPageCountAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT page_count FROM report_service.artifacts WHERE report_job_id = @ReportJobId",
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    /// <summary>One answer of the status read.</summary>
    private sealed record Answer(
        string Status,
        string? Stage,
        long RecordsRead,
        long? RecordsTotal,
        int BlocksCompleted,
        int? BlocksTotal,
        long PagesGenerated,
        string? ErrorCode,
        string? FinishedAt,
        IReadOnlyList<OutputAnswer> Outputs);

    /// <summary>One requested output of an answer.</summary>
    private sealed record OutputAnswer(string Format, string Status, string? ArtifactId, string? ErrorCode);

    /// <summary>The identifiers of one accepted job.</summary>
    private sealed record JobAccepted(string ReportJobId, string OutputId);
}
