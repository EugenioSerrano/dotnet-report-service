namespace ReportService.Templates;

/// <summary>
/// The prepared packages of one process: at most 16, least recently used first
/// out, keyed by the package's hash, its engine and the engine's version. Two
/// versions of one package are two entries; a package reprepared with the same
/// key is never parsed twice.
/// </summary>
public sealed class PreparedTemplateCache(int capacity = 16)
{
    /// <summary>How many prepared packages the cache holds.</summary>
    public const int DefaultCapacity = 16;

    private readonly Dictionary<string, LinkedListNode<Entry>> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> order = new();
    private readonly Lock gate = new();
    private readonly int capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    /// <summary>How many prepared packages the cache holds now.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    /// <summary>
    /// Returns the prepared package of <paramref name="packageSha256"/>, preparing
    /// it with <paramref name="prepare"/> when it is not held yet, and marks it as
    /// the most recently used.
    /// </summary>
    public PreparedTemplate GetOrAdd(string packageSha256, Func<PreparedTemplate> prepare)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSha256);
        ArgumentNullException.ThrowIfNull(prepare);

        var key = packageSha256;
        lock (gate)
        {
            if (entries.TryGetValue(key, out var held))
            {
                order.Remove(held);
                order.AddFirst(held);
                return held.Value.Template;
            }
        }

        // Preparing runs outside the lock: parsing a package must never block another render.
        var template = prepare();

        lock (gate)
        {
            if (entries.TryGetValue(key, out var held))
            {
                order.Remove(held);
                order.AddFirst(held);
                return held.Value.Template;
            }

            var node = order.AddFirst(new Entry(key, template));
            entries[key] = node;
            while (entries.Count > capacity && order.Last is { } last)
            {
                order.RemoveLast();
                entries.Remove(last.Value.Key);
            }

            return template;
        }
    }

    private sealed record Entry(string Key, PreparedTemplate Template);
}
