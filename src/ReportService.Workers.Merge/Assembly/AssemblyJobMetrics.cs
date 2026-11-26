using System.Diagnostics.Metrics;
using ReportService.Pdf.Assembly;
using ReportService.ServiceDefaults;

namespace ReportService.Workers.Merge.Assembly;

/// <summary>
/// The instruments of one assembly: the wall time of each stage, the parts, pages
/// and batches it handled, the temporary and output bytes it produced, the memory
/// it held and its verification verdict. Every tag is a fixed stage or result word,
/// never an identifier, a key or a path, so a measurement carries no data.
/// </summary>
public sealed class AssemblyJobMetrics : IDisposable
{
    /// <summary>The histogram of how long one stage of an assembly took.</summary>
    public const string StageDurationInstrumentName = "reportservice.pdf.assembly.duration";

    /// <summary>The histogram of the parts one assembly held.</summary>
    public const string PartsInstrumentName = "reportservice.pdf.assembly.parts";

    /// <summary>The histogram of the pages one assembly produced.</summary>
    public const string PagesInstrumentName = "reportservice.pdf.assembly.pages";

    /// <summary>The histogram of the staging batches one assembly wrote.</summary>
    public const string BatchesInstrumentName = "reportservice.pdf.assembly.batches";

    /// <summary>The histogram of the temporary disk one assembly reached.</summary>
    public const string TemporaryBytesInstrumentName = "reportservice.pdf.assembly.temporary_bytes";

    /// <summary>The histogram of the assembled document's size.</summary>
    public const string OutputBytesInstrumentName = "reportservice.pdf.assembly.output_bytes";

    /// <summary>The histogram of the managed heap one assembly observed.</summary>
    public const string HeapInstrumentName = "reportservice.pdf.assembly.managed_heap_bytes";

    /// <summary>The histogram of the working set one assembly observed.</summary>
    public const string WorkingSetInstrumentName = "reportservice.pdf.assembly.working_set_bytes";

    /// <summary>The counter of assembly verification verdicts, by result and reason.</summary>
    public const string VerificationInstrumentName = "reportservice.pdf.assembly.verification";

    /// <summary>The stage tag of checking the inputs.</summary>
    public const string InputsStage = "inputs";

    /// <summary>The stage tag of staging and stamping the batches.</summary>
    public const string BatchesStage = "batches";

    /// <summary>The stage tag of the final merge.</summary>
    public const string FinalMergeStage = "final-merge";

    /// <summary>The stage tag of the structural verification.</summary>
    public const string VerificationStage = "verification";

    /// <summary>The result tag of a verification that passed.</summary>
    public const string PassResult = "pass";

    /// <summary>The result tag of a verification that failed.</summary>
    public const string FailResult = "fail";

    /// <summary>The reason tag of a verification that failed for no named reason.</summary>
    public const string NoReason = "none";

    private readonly Meter _meter;
    private readonly Histogram<double> _stageDuration;
    private readonly Histogram<long> _parts;
    private readonly Histogram<long> _pages;
    private readonly Histogram<long> _batches;
    private readonly Histogram<long> _temporaryBytes;
    private readonly Histogram<long> _outputBytes;
    private readonly Histogram<long> _heap;
    private readonly Histogram<long> _workingSet;
    private readonly Counter<long> _verification;

    /// <summary>Creates the host's instruments on the identity it carries.</summary>
    /// <param name="identity">The identity of the host that owns the instruments.</param>
    public AssemblyJobMetrics(MeterIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _meter = new Meter(identity.Name);
        _stageDuration = _meter.CreateHistogram<double>(
            StageDurationInstrumentName,
            unit: "s",
            description: "How long one stage of an assembly took, by stage.");
        _parts = _meter.CreateHistogram<long>(PartsInstrumentName, unit: "{part}", description: "Parts one assembly held.");
        _pages = _meter.CreateHistogram<long>(PagesInstrumentName, unit: "{page}", description: "Pages one assembly produced.");
        _batches = _meter.CreateHistogram<long>(BatchesInstrumentName, unit: "{batch}", description: "Staging batches one assembly wrote.");
        _temporaryBytes = _meter.CreateHistogram<long>(
            TemporaryBytesInstrumentName,
            unit: "By",
            description: "The temporary disk one assembly reached.");
        _outputBytes = _meter.CreateHistogram<long>(
            OutputBytesInstrumentName,
            unit: "By",
            description: "The size of one assembled document.");
        _heap = _meter.CreateHistogram<long>(
            HeapInstrumentName,
            unit: "By",
            description: "The managed heap one assembly observed.");
        _workingSet = _meter.CreateHistogram<long>(
            WorkingSetInstrumentName,
            unit: "By",
            description: "The working set one assembly observed.");
        _verification = _meter.CreateCounter<long>(
            VerificationInstrumentName,
            unit: "{verification}",
            description: "Assembled documents verified, by result and reason.");
    }

    /// <summary>Records the measurements and verdict of one assembly.</summary>
    /// <param name="metrics">The assembly's measurements.</param>
    /// <param name="verdict">The structural verification's verdict, or <see langword="null"/> when the assembly did not reach it.</param>
    public void Record(AssemblyMetrics metrics, AssemblyVerdict? verdict)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        Stage(InputsStage, metrics.InputsCheck);
        Stage(BatchesStage, metrics.BatchesTime);
        Stage(FinalMergeStage, metrics.FinalMerge);
        Stage(VerificationStage, metrics.Verification);
        _parts.Record(metrics.Parts);
        _pages.Record(metrics.Pages);
        _batches.Record(metrics.Batches);
        _temporaryBytes.Record(metrics.TemporaryPeakBytes);
        _outputBytes.Record(metrics.OutputBytes);
        _heap.Record(metrics.PeakManagedBytes);
        _workingSet.Record(metrics.PeakWorkingSetBytes);
        if (verdict is not null)
        {
            _verification.Add(
                1,
                new KeyValuePair<string, object?>("result", verdict.Passed ? PassResult : FailResult),
                new KeyValuePair<string, object?>("reason", verdict.Reason ?? NoReason));
        }
    }

    private void Stage(string stage, TimeSpan elapsed) =>
        _stageDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("stage", stage));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
