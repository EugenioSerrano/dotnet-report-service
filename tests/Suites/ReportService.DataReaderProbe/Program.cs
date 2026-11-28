using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ReportService.Data;
using ReportService.Data.Partitioning;
using ReportService.DataContract;
using ReportService.DataReaderProbe;

// Usage: ReportService.DataReaderProbe <snapshot> [--schema]
// Reads the snapshot (gzip when its name ends in .gz) through the product reader and partition
// pass - validating every record against the pattern schema when --schema is given - and prints
// one JSON line: the outcome, the refusal's code, limit and reason, the records read, the peak
// resident memory of this process and the seconds taken. The exit code is 0 whatever the
// outcome; a failure of the probe itself exits 2.
if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--schema"))
{
    Console.Error.WriteLine("usage: ReportService.DataReaderProbe <snapshot> [--schema]");
    return 2;
}

var path = args[0];
using var contract = args.Length == 2 ? Compile() : null;
var clock = Stopwatch.StartNew();
long records = 0;
string outcome = "ok";
string? code = null, limit = null, reason = null;
try
{
    await using var file = File.OpenRead(path);
    await using Stream source = path.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(file, CompressionMode.Decompress) : file;
    var spec = new PartitionSpec(GroupKeyField: null, AmountField: null, PartitionLimits.Default);
    var partition = await StreamingPartitioner.PartitionAsync(Validated(HeaderItemsReader.ReadAsync(source, CancellationToken.None)), new NullSink(), spec, CancellationToken.None);
    records = partition.Index.Blocks.Sum(block => (long)block.RecordCount);
}
catch (DataContractViolation violation)
{
    outcome = "refused";
    code = violation.Code;
    limit = violation.Limit;
    reason = violation.Reason;
}

clock.Stop();
var line = JsonSerializer.Serialize(new Dictionary<string, object?>
{
    ["outcome"] = outcome,
    ["code"] = code,
    ["limit"] = limit,
    ["reason"] = reason,
    ["records"] = records,
    ["peakRssBytes"] = PeakResidentBytes(),
    ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 2),
});
Console.WriteLine(line);
return 0;

async IAsyncEnumerable<RawRecord> Validated(IAsyncEnumerable<RawRecord> values)
{
    await foreach (var value in values)
    {
        if (contract is not null && value.Ordinal > 0)
        {
            var check = contract.CheckRecord(value.Json);
            if (!check.IsValid)
            {
                throw new DataContractViolation(DataContractViolation.DataInvalid, "schema-violation", null, value.Ordinal, value.Offset, check.Path, check.Keyword);
            }
        }

        yield return value;
    }
}

static PreparedDataContract Compile()
{
    var compiled = DataContractCompiler.Compile(Encoding.UTF8.GetBytes(HostileCases.PatternSchema), "probe", "schema.json");
    return compiled.Contract ?? throw new InvalidOperationException("the probe's schema did not compile: " + compiled.Profile.Rule);
}

// The process's own high-water mark: VmHWM on Linux, the peak working set elsewhere.
static long PeakResidentBytes()
{
    const string status = "/proc/self/status";
    if (File.Exists(status))
    {
        foreach (var entry in File.ReadLines(status))
        {
            if (entry.StartsWith("VmHWM:", StringComparison.Ordinal))
            {
                var kib = entry["VmHWM:".Length..].Trim().Split(' ')[0];
                return long.Parse(kib, CultureInfo.InvariantCulture) * 1024;
            }
        }
    }

    return Process.GetCurrentProcess().PeakWorkingSet64;
}

/// <summary>A block sink that keeps nothing, so the figure is the pass's own memory.</summary>
internal sealed class NullSink : IBlockSink
{
    /// <inheritdoc />
    public Stream OpenBlock(int blockIndex, out string key)
    {
        key = string.Create(CultureInfo.InvariantCulture, $"block/probe/{blockIndex}/aa");
        return Stream.Null;
    }
}
