using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Data;
using ReportService.Data.Partitioning;
using ReportService.Storage;
using ReportService.Storage.Filesystem;

namespace ReportService.EndToEndTests;

/// <summary>
/// The datasets and the real-storage harness the streaming-partition scale and restart
/// classes share: a large multi-block snapshot written to a temporary file (ordinary or
/// grouped, plain or gzip) with the amounts kept so a case computes the opening
/// carried-forward independently, a filesystem object store over a temporary root, and
/// the small read-back and oracle helpers. Nothing reads an expectation from the
/// partitioner — the amounts, the prefix sums and the digests are computed here.
/// </summary>
internal sealed class StreamingPartitionDataset : IDisposable
{
    private const string Header = """{"title":"Large statement","currency":"EUR"}""";

    private readonly string _path;

    private StreamingPartitionDataset(string path, bool gzip, IReadOnlyList<decimal> amounts, IReadOnlyList<string?> groups)
    {
        _path = path;
        Gzip = gzip;
        Amounts = amounts;
        Groups = groups;
    }

    /// <summary>Whether the snapshot file is a gzip stream.</summary>
    public bool Gzip { get; }

    /// <summary>The amount of each record, ordinal 1..N, for the independent carried-forward oracle.</summary>
    public IReadOnlyList<decimal> Amounts { get; }

    /// <summary>The group key of each record, or null when the dataset has no groups.</summary>
    public IReadOnlyList<string?> Groups { get; }

    /// <summary>The number of records in the snapshot.</summary>
    public int RecordCount => Amounts.Count;

    /// <summary>The exact running sum of the amounts of ordinals 1..<paramref name="recordsBefore"/>.</summary>
    public decimal CarriedForwardBefore(int recordsBefore)
    {
        var sum = 0m;
        for (var i = 0; i < recordsBefore && i < Amounts.Count; i++)
        {
            sum += Amounts[i];
        }

        return sum;
    }

    /// <summary>A reader over the snapshot, decompressing a gzip file as the caller must.</summary>
    public IAsyncEnumerable<RawRecord> Read(CancellationToken cancellationToken, out IDisposable handle)
    {
        Stream source = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Gzip)
        {
            source = new GZipStream(source, CompressionMode.Decompress);
        }

        handle = source;
        return HeaderItemsReader.ReadAsync(source, cancellationToken);
    }

    /// <summary>
    /// A grouped, amount-bearing snapshot of <paramref name="recordCount"/> records in
    /// groups of <paramref name="groupSize"/>, with one group of <paramref name="largeGroupSize"/>
    /// records so a group spans a block under the default limits. Deterministic.
    /// </summary>
    public static StreamingPartitionDataset Grouped(int recordCount, int groupSize, int largeGroupSize, bool gzip)
    {
        var amounts = new List<decimal>(recordCount);
        var groups = new List<string?>(recordCount);
        var path = Path.Combine(Path.GetTempPath(), "streaming-partition", Guid.NewGuid().ToString("N") + ".json" + (gzip ? ".gz" : string.Empty));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        Write(path, gzip, writer =>
        {
            var seq = 0;
            var groupIndex = 0;
            // One deliberately large group first, so it spans more than one block under the default limits.
            seq = EmitGroup(writer, "g-large", largeGroupSize, ref seq, amounts, groups, first: true);
            var remaining = recordCount - largeGroupSize;
            while (remaining > 0)
            {
                var size = Math.Min(groupSize, remaining);
                seq = EmitGroup(writer, "g" + groupIndex, size, ref seq, amounts, groups, first: false);
                remaining -= size;
                groupIndex++;
            }
        });

        return new StreamingPartitionDataset(path, gzip, amounts, groups);
    }

    /// <summary>An ordinary, amount-bearing snapshot of <paramref name="recordCount"/> records with no group key. Deterministic.</summary>
    public static StreamingPartitionDataset Plain(int recordCount, bool gzip)
    {
        var amounts = new List<decimal>(recordCount);
        var groups = new List<string?>(recordCount);
        var path = Path.Combine(Path.GetTempPath(), "streaming-partition", Guid.NewGuid().ToString("N") + ".json" + (gzip ? ".gz" : string.Empty));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        Write(path, gzip, writer =>
        {
            var first = true;
            for (var seq = 1; seq <= recordCount; seq++)
            {
                var amount = Amount(seq);
                amounts.Add(amount);
                groups.Add(null);
                WriteRecord(writer, first, $$"""{"sequence":{{seq}},"amount":{{Text(amount)}}}""");
                first = false;
            }
        });

        return new StreamingPartitionDataset(path, gzip, amounts, groups);
    }

    private static int EmitGroup(TextWriter writer, string group, int size, ref int seq, List<decimal> amounts, List<string?> groups, bool first)
    {
        for (var i = 0; i < size; i++)
        {
            seq++;
            var amount = Amount(seq);
            amounts.Add(amount);
            groups.Add(group);
            WriteRecord(writer, first && i == 0 && seq == 1, $$"""{"sequence":{{seq}},"group":"{{group}}","amount":{{Text(amount)}}}""");
        }

        return seq;
    }

    // A small, varied, exactly-representable decimal so the running sum is a meaningful oracle.
    private static decimal Amount(int seq) => (seq % 97 + 1) / 100m;

    private static string Text(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void WriteRecord(TextWriter writer, bool first, string record)
    {
        if (!first)
        {
            writer.Write(',');
        }

        writer.Write(record);
    }

    private static void Write(string path, bool gzip, Action<TextWriter> body)
    {
        Stream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Stream sink = gzip ? new GZipStream(file, CompressionLevel.Fastest) : file;
        using var writer = new StreamWriter(sink, new UTF8Encoding(false));
        writer.Write("""{"contractVersion":"1.0","header":""");
        writer.Write(Header);
        writer.Write(""","items":[""");
        body(writer);
        writer.Write("]}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A leftover temporary file is the operating system's to sweep; it never fails a run.
        }
    }
}

/// <summary>A filesystem object store over a temporary root, removed when the harness is disposed.</summary>
internal sealed class TemporaryObjectStore : IDisposable
{
    private readonly string _root;

    private TemporaryObjectStore(string root, IObjectStore store)
    {
        _root = root;
        Store = store;
    }

    /// <summary>The store over the temporary root.</summary>
    public IObjectStore Store { get; }

    /// <summary>Opens a store over a fresh temporary root.</summary>
    public static TemporaryObjectStore Open()
    {
        var root = Path.Combine(Path.GetTempPath(), "streaming-partition-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = root, CreateIfMissing = true },
            root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        return new TemporaryObjectStore(root, store);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Left for the operating system to sweep; cleanup never fails a run.
        }
    }
}
