using System.Diagnostics;

namespace ReportService.Pdf.Assembly;

/// <summary>
/// The measurements of one assembly: what it read, how many batches it staged,
/// how long each stage took, the temporary disk it reached and the memory it
/// held. The figures are recorded with the job's own metrics and carry no record
/// value, storage key or path.
/// </summary>
public sealed class AssemblyMetrics
{
    private readonly Stopwatch _stage = new();

    /// <summary>How many parts were assembled.</summary>
    public int Parts { get; private set; }

    /// <summary>How many pages the assembled document holds.</summary>
    public int Pages { get; private set; }

    /// <summary>How many bytes the parts hold, as their commits recorded.</summary>
    public long PartBytes { get; private set; }

    /// <summary>How many staging batches were written.</summary>
    public int Batches { get; private set; }

    /// <summary>How long checking the inputs took.</summary>
    public TimeSpan InputsCheck { get; private set; }

    /// <summary>How long staging, stamping and saving the batches took.</summary>
    public TimeSpan BatchesTime { get; private set; }

    /// <summary>How long importing the batches into the final document took.</summary>
    public TimeSpan FinalMerge { get; private set; }

    /// <summary>How long structurally verifying the assembled document took.</summary>
    public TimeSpan Verification { get; private set; }

    /// <summary>The largest number of bytes the assembly held on temporary disk.</summary>
    public long TemporaryPeakBytes { get; private set; }

    /// <summary>The size of the assembled document.</summary>
    public long OutputBytes { get; private set; }

    /// <summary>The largest managed heap the assembly observed.</summary>
    public long PeakManagedBytes { get; private set; }

    /// <summary>The largest working set the assembly observed.</summary>
    public long PeakWorkingSetBytes { get; private set; }

    /// <summary>The version of the writing library, as its assembly reports it.</summary>
    public string PdfLibraryVersion { get; private set; } = "unknown";

    /// <summary>The SHA-256 of the packaged stamp font, lower-case hexadecimal.</summary>
    public string StampFontSha256 { get; private set; } = string.Empty;

    /// <summary>The generator that drew the document's codes, when the template declares one.</summary>
    public ReportService.Pdf.Verification.PageCodes.PageCodeGeneratorDescriptor? CodeGenerator { get; private set; }

    /// <summary>How many pages carry a code.</summary>
    public int CodePages { get; private set; }

    /// <summary>The codes' stored bytes, summed.</summary>
    public long CodeBytes { get; private set; }

    /// <summary>The time evaluating the sources and generating the codes took, summed.</summary>
    public TimeSpan CodeGeneration { get; private set; }

    /// <summary>How many pages' codes could not be drawn.</summary>
    public int CodeFailures { get; private set; }

    /// <summary>Records the figures of the parts and the font.</summary>
    /// <param name="parts">The parts, in manifest order.</param>
    /// <param name="numbering">The numbering contract.</param>
    public void Begin(IReadOnlyList<AssemblyPart> parts, PageNumbering? numbering, PageCodes.AssemblyPageCode? pageCode = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        Parts = parts.Count;
        PartBytes = parts.Sum(part => part.SizeBytes);
        StampFontSha256 = numbering?.Font.Sha256 ?? string.Empty;
        CodeGenerator = pageCode?.Generator.Descriptor;
        PdfLibraryVersion = typeof(PdfSharp.Pdf.PdfDocument).Assembly
            .GetName().Version?.ToString() ?? "unknown";
        Observe();
    }

    /// <summary>Runs one stage, records its duration and observes the process's memory.</summary>
    /// <param name="stage">The stage.</param>
    /// <param name="action">The work of the stage.</param>
    public async Task MeasureAsync(AssemblyStage stage, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _stage.Restart();
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _stage.Stop();
            switch (stage)
            {
                case AssemblyStage.InputsCheck:
                    InputsCheck = _stage.Elapsed;
                    break;
                case AssemblyStage.Batches:
                    BatchesTime = _stage.Elapsed;
                    break;
                case AssemblyStage.FinalMerge:
                    FinalMerge = _stage.Elapsed;
                    break;
                case AssemblyStage.Verification:
                    Verification = _stage.Elapsed;
                    break;
            }

            Observe();
        }
    }

    /// <summary>Records how many pages the assembled document holds.</summary>
    /// <param name="pages">The assembled document's page count.</param>
    public void RecordPages(int pages) => Pages = pages;

    /// <summary>Records one page code drawn: its stored size and the time its content took.</summary>
    /// <param name="storedBytes">The form's stored size.</param>
    /// <param name="generation">How long the source evaluation and the generation took.</param>
    public void RecordCode(long storedBytes, TimeSpan generation)
    {
        CodePages++;
        CodeBytes += storedBytes;
        CodeGeneration += generation;
    }

    /// <summary>Records a page whose code could not be drawn.</summary>
    public void RecordCodeFailure() => CodeFailures++;

    /// <summary>Records one written batch and the temporary bytes it reached.</summary>
    /// <param name="temporaryBytes">The bytes the assembly's folder holds now.</param>
    public void RecordBatch(long temporaryBytes)
    {
        Batches++;
        RecordTemporary(temporaryBytes);
    }

    /// <summary>Records the temporary bytes the assembly's folder holds now.</summary>
    /// <param name="temporaryBytes">The bytes the assembly's folder holds now.</param>
    public void RecordTemporary(long temporaryBytes) =>
        TemporaryPeakBytes = Math.Max(TemporaryPeakBytes, temporaryBytes);

    /// <summary>Records the assembled document's size.</summary>
    /// <param name="bytes">The assembled document's size.</param>
    public void RecordOutput(long bytes)
    {
        OutputBytes = bytes;
        Observe();
    }

    private void Observe()
    {
        PeakManagedBytes = Math.Max(PeakManagedBytes, GC.GetTotalMemory(forceFullCollection: false));
        PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, Process.GetCurrentProcess().WorkingSet64);
    }
}

/// <summary>The stages an assembly measures.</summary>
public enum AssemblyStage
{
    /// <summary>Checking the parts' bytes and pages against their commits.</summary>
    InputsCheck,

    /// <summary>Staging, stamping and saving the batches.</summary>
    Batches,

    /// <summary>Importing the batch files into the final document.</summary>
    FinalMerge,

    /// <summary>Structurally verifying the assembled document.</summary>
    Verification,
}
