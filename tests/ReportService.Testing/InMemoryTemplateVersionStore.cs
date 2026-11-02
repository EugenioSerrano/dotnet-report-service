using ReportService.TemplatePackages.Records;

namespace ReportService.Testing;

/// <summary>
/// The taken template versions held in memory, for a test that needs the record a
/// take would have left without a database. The insert-once rule is the real one:
/// the first insert of an identity is the record that stays and a second insert of
/// the same identity answers it instead of replacing it. Each call is counted, so a
/// case can prove an operation never reached the store at all.
/// </summary>
public sealed class InMemoryTemplateVersionStore : ITemplateVersionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string TemplateId, string Version), TemplateVersionRecord> _records = [];
    private int _findCalls;
    private int _insertCalls;

    /// <summary>How many find calls this store received.</summary>
    public int FindCalls => Volatile.Read(ref _findCalls);

    /// <summary>How many insert calls this store received.</summary>
    public int InsertCalls => Volatile.Read(ref _insertCalls);

    /// <summary>The records this store holds, in no particular order.</summary>
    public IReadOnlyList<TemplateVersionRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return [.. _records.Values];
            }
        }
    }

    /// <summary>Holds <paramref name="record"/> as the take that wrote it would have left it.</summary>
    /// <param name="record">The record to hold.</param>
    public void Seed(TemplateVersionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            _records[(record.TemplateId, record.Version)] = record;
        }
    }

    /// <inheritdoc />
    public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        Interlocked.Increment(ref _findCalls);
        lock (_gate)
        {
            return Task.FromResult(_records.GetValueOrDefault((templateId, version)));
        }
    }

    /// <inheritdoc />
    public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        Interlocked.Increment(ref _insertCalls);
        lock (_gate)
        {
            var identity = (record.TemplateId, record.Version);
            if (_records.TryGetValue(identity, out var existing))
            {
                return Task.FromResult(TemplateVersionInsert.Existing(existing));
            }

            _records[identity] = record;
            return Task.FromResult(TemplateVersionInsert.Inserted(record));
        }
    }
}
