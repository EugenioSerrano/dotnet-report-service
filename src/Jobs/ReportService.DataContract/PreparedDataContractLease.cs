namespace ReportService.DataContract;

/// <summary>
/// The right to use one prepared contract for as long as the work that asked for it
/// needs it. The cache keeps the contract alive across leases; an entry the cache evicts
/// while it is still leased is disposed when the last lease ends, never under its user.
/// </summary>
public sealed class PreparedDataContractLease : IDisposable
{
    private readonly PreparedDataContractCache _cache;
    private readonly PreparedDataContractCache.Entry _entry;
    private bool _disposed;

    internal PreparedDataContractLease(PreparedDataContractCache cache, PreparedDataContractCache.Entry entry)
    {
        _cache = cache;
        _entry = entry;
    }

    /// <summary>The compilation this lease holds, contract or refusal.</summary>
    public ContractCompilation Compilation => _entry.Compilation;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Release(_entry);
    }
}
