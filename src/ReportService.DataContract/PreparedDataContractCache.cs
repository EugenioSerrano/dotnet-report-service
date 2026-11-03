namespace ReportService.DataContract;

/// <summary>
/// The contracts a worker has prepared, bounded and kept by the least recently used
/// entry: a schema is compiled once per package and schema path for as long as it is
/// among the entries the cache holds, however many values are evaluated against it. An
/// entry is evicted by use, and an evicted contract is disposed only once no lease holds
/// it. A refusal is kept like a contract, so a package that is refused is not read and
/// refused again for every value.
/// </summary>
public sealed class PreparedDataContractCache : IDisposable
{
    /// <summary>The most entries the cache holds.</summary>
    public const int MaximumEntries = 16;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = [];
    private int _compilationCount;
    private bool _disposed;

    /// <summary>How many compilations the cache has performed since it was created.</summary>
    public int CompilationCount
    {
        get
        {
            lock (_gate)
            {
                return _compilationCount;
            }
        }
    }

    /// <summary>Takes a lease on the contract of a package's schema, compiling it on the first use.</summary>
    /// <param name="packageSha256">The SHA-256 of the package the schema belongs to.</param>
    /// <param name="schemaPath">The path of the schema inside the package.</param>
    /// <param name="schema">The schema document, as the package holds it.</param>
    /// <returns>The lease, which releases the entry when it is disposed.</returns>
    public PreparedDataContractLease Lease(
        string packageSha256,
        string schemaPath,
        ReadOnlyMemory<byte> schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaPath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = packageSha256 + "\n" + schemaPath + "\n" + DataContractCompiler.LibraryVersion;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var known))
            {
                Reuse(known);
                return new PreparedDataContractLease(this, known);
            }
        }

        var compilation = DataContractCompiler.Compile(schema, packageSha256, schemaPath);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var known))
            {
                // Another caller compiled the same schema while this one was busy.
                Reuse(known);
                compilation.Contract?.Dispose();
                return new PreparedDataContractLease(this, known);
            }

            _compilationCount++;
            if (_entries.Count >= MaximumEntries)
            {
                Evict();
            }

            var entry = new Entry(key, compilation);
            entry.Node = _order.AddFirst(entry);
            entry.Leases = 1;
            _entries[key] = entry;
            return new PreparedDataContractLease(this, entry);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                entry.Evicted = true;
                if (entry.Leases == 0)
                {
                    entry.Compilation.Contract?.Dispose();
                }
            }

            _entries.Clear();
            _order.Clear();
        }
    }

    internal void Release(Entry entry)
    {
        lock (_gate)
        {
            entry.Leases--;
            if (entry.Evicted && entry.Leases == 0)
            {
                entry.Compilation.Contract?.Dispose();
            }
        }
    }

    private void Reuse(Entry entry)
    {
        entry.Leases++;
        if (entry.Node is not null)
        {
            _order.Remove(entry.Node);
            entry.Node = _order.AddFirst(entry);
        }
    }

    private void Evict()
    {
        var last = _order.Last;
        if (last is null)
        {
            return;
        }

        _order.RemoveLast();
        _entries.Remove(last.Value.Key);
        last.Value.Evicted = true;
        if (last.Value.Leases == 0)
        {
            last.Value.Compilation.Contract?.Dispose();
        }
    }

    /// <summary>One prepared contract, and how many leases hold it.</summary>
    internal sealed class Entry
    {
        public Entry(string key, ContractCompilation compilation)
        {
            Key = key;
            Compilation = compilation;
        }

        public string Key { get; }

        public ContractCompilation Compilation { get; }

        public LinkedListNode<Entry>? Node { get; set; }

        public int Leases { get; set; }

        public bool Evicted { get; set; }
    }
}
