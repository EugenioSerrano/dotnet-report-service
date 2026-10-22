using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XlsxWriterSpike.Measurement;

/// <summary>One measured <c>write</c> run, printed as one JSON line. Paths are file names only.</summary>
internal sealed record RunResult
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Candidate name.</summary>
    public required string Candidate { get; init; }

    /// <summary>Column profile name.</summary>
    public required string Profile { get; init; }

    /// <summary>Dataset file name.</summary>
    public required string Dataset { get; init; }

    /// <summary>Data rows written.</summary>
    public long RowsWritten { get; init; }

    /// <summary>Sheets written.</summary>
    public int Sheets { get; init; }

    /// <summary><c>ok</c>, <c>OUTPUT_LIMIT_EXCEEDED</c> or <c>failed</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Error code, when not ok.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Limit reason, when <c>OUTPUT_LIMIT_EXCEEDED</c>.</summary>
    public string? ErrorReason { get; init; }

    /// <summary>Record number of the error.</summary>
    public long? ErrorRow { get; init; }

    /// <summary>Column of the error.</summary>
    public string? ErrorColumn { get; init; }

    /// <summary>One-line error message.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Wall time of the write.</summary>
    public double ElapsedSeconds { get; init; }

    /// <summary>Data rows per second.</summary>
    public double RowsPerSecond { get; init; }

    /// <summary>Largest sampled managed heap in use, MiB.</summary>
    public double ManagedHeapPeakMiB { get; init; }

    /// <summary>Process peak working set / RSS, MiB.</summary>
    public double ProcessPeakRssMiB { get; init; }

    /// <summary>Always <c>null</c>: no browser runs in this experiment.</summary>
    public double? ChromiumTreePeakRssMiB { get; init; }

    /// <summary>Largest sampled size of the run's temporary directory, MiB (<c>null</c> = not measured).</summary>
    public double? TempDiskPeakMiB { get; init; }

    /// <summary>Output file size, MiB.</summary>
    public double OutputMiB { get; init; }

    /// <summary>Total bytes allocated by the process, MiB.</summary>
    public double AllocatedMiB { get; init; }

    /// <summary>Generation 2 collections during the process lifetime.</summary>
    public int Gen2Collections { get; init; }

    /// <summary>Times the source was enumerated.</summary>
    public int SourceEnumerations { get; init; }

    /// <summary>Operating system description.</summary>
    public string Os { get; init; } = RuntimeInformation.OSDescription;

    /// <summary>Runtime version.</summary>
    public string Runtime { get; init; } = RuntimeInformation.FrameworkDescription;

    /// <summary>Garbage collector mode.</summary>
    public string GcMode { get; init; } = DescribeGc();

    /// <summary>Serializes to one JSON line.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Parses one JSON line.</summary>
    public static RunResult? FromJson(string json) => JsonSerializer.Deserialize<RunResult>(json, Options);

    /// <summary>Bytes to MiB, three decimals.</summary>
    public static double MiB(long bytes) => Math.Round(bytes / 1024d / 1024d, 3);

    private static string DescribeGc() =>
        (GCSettings.IsServerGC ? "server" : "workstation")
        + (string.Equals(AppContext.GetData("System.GC.Concurrent")?.ToString(), "true", StringComparison.OrdinalIgnoreCase)
            ? ", concurrent"
            : ", non-concurrent");
}
