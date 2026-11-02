using ReportService.Storage;

namespace ReportService.Uploads;

/// <summary>
/// The upload record store the tests use in place of the database. It answers
/// the same contract as the real store — owner-scoped reads, one conditional
/// write per step, one request per confirmed upload, one snapshot per upload, and
/// the same refusal of every write that arrives at or after the end of the
/// upload's session or of its verification — and it records the calls and the
/// requests it received, so a test can assert that a refused call wrote nothing
/// and that a repeated step changed nothing. The shared contract runs against
/// this class and against the real store, so the double cannot drift away from
/// what it stands in for.
/// </summary>
public sealed class InMemoryUploadStore : IUploadStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, UploadRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SnapshotRecord> _snapshots = new(StringComparer.Ordinal);
    private readonly List<RecordedIntent> _intents = [];
    private int _createCalls;
    private int _findCalls;

    /// <summary>A request the store recorded when an upload was confirmed.</summary>
    /// <param name="UploadId">The upload the request names.</param>
    /// <param name="CreatedAt">When the request was recorded, in UTC.</param>
    public sealed record RecordedIntent(string UploadId, DateTimeOffset CreatedAt);

    /// <summary>How many times <see cref="CreateAsync"/> was called.</summary>
    public int CreateCalls => Volatile.Read(ref _createCalls);

    /// <summary>How many times <see cref="FindAsync"/> was called.</summary>
    public int FindCalls => Volatile.Read(ref _findCalls);

    /// <summary>The records the store holds, each with its snapshot when it has one.</summary>
    public IReadOnlyCollection<UploadRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return [.. _records.Values.Select(WithSnapshot)];
            }
        }
    }

    /// <summary>The requests the store recorded, in the order they were recorded.</summary>
    public IReadOnlyList<RecordedIntent> Intents
    {
        get
        {
            lock (_gate)
            {
                return [.. _intents];
            }
        }
    }

    /// <inheritdoc />
    public Task CreateAsync(UploadRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        Interlocked.Increment(ref _createCalls);
        lock (_gate)
        {
            return _records.TryAdd(record.UploadId, record)
                ? Task.CompletedTask
                : throw new InvalidOperationException("the upload identifier is already in use");
        }
    }

    /// <inheritdoc />
    public Task<UploadRecord?> FindAsync(string uploadId, string consumerId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _findCalls);
        lock (_gate)
        {
            return Task.FromResult(
                _records.TryGetValue(uploadId, out var record)
                && string.Equals(record.ConsumerId, consumerId, StringComparison.Ordinal)
                    ? WithSnapshot(record)
                    : null);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryStartTransferAsync(
        string uploadId,
        string consumerId,
        UploadContentEncoding encoding,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        Task.FromResult(Update(uploadId, record =>
            record.Status == UploadStatus.Created
            && now < record.ExpiresAt
            && string.Equals(record.ConsumerId, consumerId, StringComparison.Ordinal)
                ? record with { Status = UploadStatus.Uploading, ContentEncoding = encoding }
                : null));

    /// <inheritdoc />
    public Task<bool> RecordTransferAsync(
        string uploadId,
        UploadContentOutcome outcome,
        long receivedBytes,
        long? stagingSize,
        string? stagingSha256,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        Task.FromResult(Update(uploadId, record =>
            record.Status == UploadStatus.Uploading && record.ContentOutcome is null && now < record.ExpiresAt
                ? record with
                {
                    ContentOutcome = outcome,
                    ReceivedBytes = receivedBytes,
                    StagingSize = stagingSize,
                    StagingSha256 = stagingSha256,
                }
                : null));

    /// <inheritdoc />
    public Task<bool> RejectTransferAsync(
        string uploadId,
        UploadRejectionCode code,
        UploadRejectionReason reason,
        long receivedBytes,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        Task.FromResult(Update(uploadId, record =>
            record.Status == UploadStatus.Uploading && record.ContentOutcome is null && now < record.ExpiresAt
                ? record with
                {
                    Status = UploadStatus.Rejected,
                    RejectionCode = code,
                    RejectionReason = reason,
                    ReceivedBytes = receivedBytes,
                }
                : null));

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(
        string uploadId,
        string consumerId,
        string? declaredSha256,
        DateTimeOffset completedAt,
        DateTimeOffset now,
        DateTimeOffset verificationDeadline,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(uploadId, out var record)
                || record.Status != UploadStatus.Uploading
                || record.ContentOutcome is null
                || now >= record.ExpiresAt
                || !string.Equals(record.ConsumerId, consumerId, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            _records[uploadId] = record with
            {
                Status = UploadStatus.Verifying,
                CompletedAt = completedAt,
                VerificationDeadline = verificationDeadline,
                DeclaredSha256 = declaredSha256,
            };
            if (!_intents.Any(intent => string.Equals(intent.UploadId, uploadId, StringComparison.Ordinal)))
            {
                _intents.Add(new RecordedIntent(uploadId, completedAt));
            }

            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<UploadExpiryOutcome> ExpireIfOverdueAsync(
        string uploadId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(uploadId, out var record))
            {
                return Task.FromResult(new UploadExpiryOutcome(null, false));
            }

            if (!UploadExpiry.IsOverdue(record, now))
            {
                return Task.FromResult(new UploadExpiryOutcome(WithSnapshot(record), false));
            }

            var expired = record with { Status = UploadStatus.Expired };
            _records[uploadId] = expired;
            return Task.FromResult(new UploadExpiryOutcome(WithSnapshot(expired), true));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ExpiredUpload>> ExpireOverdueAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var due = _records.Values
                .Where(record => UploadExpiry.IsOverdue(record, now))
                .OrderBy(record => record.UploadId, StringComparer.Ordinal)
                .Take(batchSize)
                .ToList();
            foreach (var record in due)
            {
                _records[record.UploadId] = record with { Status = UploadStatus.Expired };
            }

            return Task.FromResult<IReadOnlyList<ExpiredUpload>>(
                [.. due.Select(record => new ExpiredUpload(record.UploadId, record.Status, record.StagingKey))]);
        }
    }

    /// <inheritdoc />
    public Task<UploadRecord?> LoadForVerificationAsync(string uploadId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_records.TryGetValue(uploadId, out var record) ? WithSnapshot(record) : null);
        }
    }

    /// <inheritdoc />
    public Task<SnapshotCommitResult> CommitReadyAsync(
        SnapshotRecord snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_snapshots.ContainsKey(snapshot.UploadId))
            {
                return Task.FromResult(SnapshotCommitResult.AlreadyCommitted);
            }

            if (!_records.TryGetValue(snapshot.UploadId, out var record)
                || record.Status != UploadStatus.Verifying
                || record.VerificationDeadline is not { } deadline
                || now >= deadline)
            {
                return Task.FromResult(SnapshotCommitResult.NotVerifying);
            }

            _snapshots[snapshot.UploadId] = snapshot;
            _records[snapshot.UploadId] = record with { Status = UploadStatus.Ready };
            return Task.FromResult(SnapshotCommitResult.Committed);
        }
    }

    /// <inheritdoc />
    public Task<bool> CommitRejectedAsync(
        string uploadId,
        UploadRejectionCode code,
        UploadRejectionReason reason,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        Task.FromResult(Update(uploadId, record =>
            record.Status == UploadStatus.Verifying
            && record.VerificationDeadline is { } deadline
            && now < deadline
                ? record with
                {
                    Status = UploadStatus.Rejected,
                    RejectionCode = code,
                    RejectionReason = reason,
                }
                : null));

    /// <summary>The snapshot of <paramref name="uploadId"/>, when one was committed.</summary>
    /// <param name="uploadId">The identifier of the upload.</param>
    /// <returns>The snapshot, or <see langword="null"/> when none was committed.</returns>
    public SnapshotRecord? SnapshotOf(string uploadId)
    {
        lock (_gate)
        {
            return _snapshots.GetValueOrDefault(uploadId);
        }
    }

    private bool Update(string uploadId, Func<UploadRecord, UploadRecord?> change)
    {
        ArgumentNullException.ThrowIfNull(uploadId);
        lock (_gate)
        {
            if (!_records.TryGetValue(uploadId, out var record) || change(record) is not { } changed)
            {
                return false;
            }

            _records[uploadId] = changed;
            return true;
        }
    }

    private UploadRecord WithSnapshot(UploadRecord record) =>
        _snapshots.TryGetValue(record.UploadId, out var snapshot) ? record with { Snapshot = snapshot } : record;
}
