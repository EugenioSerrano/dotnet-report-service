using System.Collections.Concurrent;
using ReportService.Api.Uploads;

namespace ReportService.Api.Tests.Uploads;

/// <summary>
/// The upload record store the component tests use in place of the database: it
/// keeps the records in memory, answers the same contract as the real store, and
/// counts the calls it received, so a test can assert that a refused request
/// wrote nothing and that an identifier which cannot exist caused no read.
/// </summary>
internal sealed class InMemoryUploadStore : IUploadStore
{
    private readonly ConcurrentDictionary<string, UploadRecord> _records = new(StringComparer.Ordinal);
    private int _createCalls;
    private int _findCalls;

    /// <summary>How many times <see cref="CreateAsync"/> was called.</summary>
    public int CreateCalls => Volatile.Read(ref _createCalls);

    /// <summary>How many times <see cref="FindAsync"/> was called.</summary>
    public int FindCalls => Volatile.Read(ref _findCalls);

    /// <summary>The records the store holds.</summary>
    public IReadOnlyCollection<UploadRecord> Records => [.. _records.Values];

    /// <inheritdoc />
    public Task CreateAsync(UploadRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        Interlocked.Increment(ref _createCalls);
        return _records.TryAdd(record.UploadId, record)
            ? Task.CompletedTask
            : throw new InvalidOperationException("the upload identifier is already in use");
    }

    /// <inheritdoc />
    public Task<UploadRecord?> FindAsync(string uploadId, string consumerId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _findCalls);
        return Task.FromResult(
            _records.TryGetValue(uploadId, out var record)
            && string.Equals(record.ConsumerId, consumerId, StringComparison.Ordinal)
                ? record
                : null);
    }
}
