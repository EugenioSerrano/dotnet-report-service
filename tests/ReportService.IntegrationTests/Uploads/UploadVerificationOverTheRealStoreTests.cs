extern alias orchestration;

using System.Globalization;
using System.IO.Compression;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.Migrations;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.Testing;
using ReportService.Uploads;
using OrchestrationMetrics = orchestration::ReportService.Workers.Orchestration.Telemetry.OrchestrationMetrics;
using VerificationOptions = orchestration::ReportService.Workers.Orchestration.Uploads.VerificationOptions;
using VerificationResult = orchestration::ReportService.Workers.Orchestration.Uploads.VerificationResult;
using UploadVerifier = orchestration::ReportService.Workers.Orchestration.Uploads.UploadVerifier;

namespace ReportService.IntegrationTests.Uploads;

/// <summary>
/// The one case the transfer level cannot see: a body that arrives complete —
/// the declared length matches the bytes sent and the transfer is recorded as
/// whole — but whose compressed unit of content is cut. With the real storage
/// provider and a real database, the upload must end rejected with no snapshot
/// record and no snapshot object, while the same object with its trailer intact
/// ends ready. This is the evidence that the refusal comes from the content's own
/// trailer and not from the body's size.
/// </summary>
public sealed class UploadVerificationOverTheRealStoreTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource? _dataSource;
    private FilesystemObjectStore? _objects;
    private string? _root;
    private string? _database;
    private UploadVerifier? _verifier;
    private OrchestrationMetrics? _metrics;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public UploadVerificationOverTheRealStoreTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _database = await _fixture.CreateDatabaseAsync(cancellationToken);
        var connectionString = _fixture.Application(_database);
        var migration = await MigrationRunner.RunAsync(connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _root = Path.Combine(
            Path.GetTempPath(),
            "report-service-tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_root);
        _objects = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = _root },
            _root,
            TimeProvider.System,
            LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning)).CreateLogger<FilesystemObjectStore>());
        _metrics = new OrchestrationMetrics();
        _verifier = new UploadVerifier(
            new PostgresUploadStore(_dataSource),
            _objects,
            Options.Create(new VerificationOptions { MaxStoredBytes = 8 << 20, MaxUncompressedBytes = 8 << 20 }),
            new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero)),
            LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning)).CreateLogger<UploadVerifier>(),
            _metrics);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _metrics?.Dispose();
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_root is not null)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not a test failure.
            }
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public async Task ACompressedBodyWhoseTrailerIsCutIsRefusedWithNoSnapshot(int dropped)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = $"upload-{dropped:x8}{new string('1', 24)}";
        var content = Payload(4096);
        var stored = Gzip(content)[..^dropped];
        await GivenVerifyingAsync(uploadId, stored, UploadContentEncoding.Gzip, Convert.ToHexStringLower(SHA256Hash(content)), cancellationToken);

        var outcome = await _verifier!.VerifyAsync(
            uploadId,
            ObjectKey.FromSegments("snapshots", uploadId, "attempt"),
            null,
            cancellationToken);

        Assert.Equal(VerificationResult.Rejected, outcome.Result);
        Assert.Equal(UploadRejectionCode.DataInvalid, outcome.Code);
        Assert.Equal(UploadRejectionReason.Truncated, outcome.Reason);
        Assert.Equal(0, await SnapshotRecordsAsync(uploadId, cancellationToken));
        Assert.Empty(await SnapshotObjectsAsync(uploadId));
        Assert.Equal(0, await StoredObjectsAsync(uploadId));
    }

    [Fact]
    public async Task TheSameObjectWithItsTrailerIntactEndsReady()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string UploadId = "upload-00000000000000000000000000000002";
        var content = Payload(4096);
        var stored = Gzip(content);
        await GivenVerifyingAsync(UploadId, stored, UploadContentEncoding.Gzip, null, cancellationToken);

        var outcome = await _verifier!.VerifyAsync(
            UploadId,
            ObjectKey.FromSegments("snapshots", UploadId, "attempt"),
            null,
            cancellationToken);

        Assert.Equal(VerificationResult.Ready, outcome.Result);
        Assert.Equal(Convert.ToHexStringLower(SHA256Hash(content)), outcome.Snapshot!.Sha256Uncompressed);
        Assert.Equal(content.Length, outcome.Snapshot.SizeUncompressed);
        Assert.Equal(1, await SnapshotRecordsAsync(UploadId, cancellationToken));
        Assert.Single(await SnapshotObjectsAsync(UploadId));
    }

    [Fact]
    public async Task ConfirmingAnUploadRecordsExactlyOneRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string UploadId = "upload-00000000000000000000000000000003";
        await GivenVerifyingAsync(UploadId, Gzip(Payload(512)), UploadContentEncoding.Gzip, null, cancellationToken);
        await GivenVerifyingAsync(UploadId, Gzip(Payload(512)), UploadContentEncoding.Gzip, null, cancellationToken);

        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        var requests = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM report_service.intents WHERE kind = 'start-upload-verification' AND subject_id = @UploadId",
            new { UploadId },
            cancellationToken: cancellationToken));

        Assert.Equal(1, requests);
    }

    private async Task GivenVerifyingAsync(
        string uploadId,
        byte[] stored,
        UploadContentEncoding encoding,
        string? declaredSha256,
        CancellationToken cancellationToken)
    {
        var store = new PostgresUploadStore(_dataSource!);
        var stagingKey = ObjectKey.FromSegments("staging", uploadId, "content");
        var record = new UploadRecord(
            uploadId,
            "dev-consumer",
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Created,
            stagingKey,
            new DateTimeOffset(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 4, 39, 23, 123, TimeSpan.Zero));

        // A second call for the same upload leaves the first record where it is.
        var existing = await store.FindAsync(uploadId, record.ConsumerId, cancellationToken);
        if (existing is not null)
        {
            return;
        }

        await store.CreateAsync(record, cancellationToken);
        var decidedAt = new DateTimeOffset(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero);
        await store.TryStartTransferAsync(uploadId, record.ConsumerId, encoding, decidedAt, cancellationToken);

        using var source = new MemoryStream(stored, writable: false);
        var written = await _objects!.WriteAsync(stagingKey, source, cancellationToken);
        Assert.True(written.Succeeded, "the staging object had to be written");

        await store.RecordTransferAsync(
            uploadId,
            UploadContentOutcome.Complete,
            written.Value!.Size,
            written.Value.Size,
            written.Value.Sha256,
            decidedAt,
            cancellationToken);
        Assert.True(await store.ConfirmAsync(
            uploadId,
            record.ConsumerId,
            declaredSha256,
            decidedAt,
            decidedAt,
            decidedAt.AddHours(6),
            cancellationToken));
    }

    private async Task<long> SnapshotRecordsAsync(string uploadId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM report_service.snapshots WHERE upload_id = @UploadId",
            new { UploadId = uploadId },
            cancellationToken: cancellationToken));
    }

    private async Task<IReadOnlyList<string>> SnapshotObjectsAsync(string uploadId)
    {
        var keys = new List<string>();
        await foreach (var info in _objects!.ListAsync($"snapshots/{uploadId}", TestContext.Current.CancellationToken))
        {
            keys.Add(info.Key.ToString());
        }

        return keys;
    }

    private async Task<long> StoredObjectsAsync(string uploadId)
    {
        var count = 0L;
        await foreach (var _ in _objects!.ListAsync($"staging/{uploadId}", TestContext.Current.CancellationToken))
        {
            count++;
        }

        return count;
    }

    private static byte[] Payload(int bytes)
    {
        var builder = new StringBuilder("{\"header\":{},\"items\":[");
        for (var index = 0; builder.Length < bytes; index++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{{\"id\":{index},\"v\":{index * 17}}},");
        }

        builder.Append("]}");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static byte[] Gzip(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    private static byte[] SHA256Hash(byte[] content) => System.Security.Cryptography.SHA256.HashData(content);
}
