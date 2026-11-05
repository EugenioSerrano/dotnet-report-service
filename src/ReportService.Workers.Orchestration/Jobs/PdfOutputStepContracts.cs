using Temporalio.Activities;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The orchestration worker's view of the one activity the PDF worker serves for a
/// job's output: printing one block. It is scheduled by name on
/// <c>report-pdf-rendering</c>, so this process never loads the PDF worker's
/// dependencies, and the records below mirror the delivered ones member for member -
/// same member names, same types - so the answer the PDF worker writes is read here
/// exactly as it was written. The integration suite compares both sides; a drift is a
/// stop, never a silent retry.
/// </summary>
public static class PdfOutputStepContracts
{
    /// <summary>The name of the block render, as the PDF worker registers it.</summary>
    public const string RenderPdfBlock = "RenderPdfBlock";
}

/// <summary>What printing one block is asked for, as the PDF worker reads it.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="OutputId">The identifier of the PDF output of the job.</param>
/// <param name="BlockIndex">The block's position in the job's partition.</param>
public sealed record RenderPdfBlockStepInput(string ReportJobId, string OutputId, int BlockIndex);

/// <summary>What the PDF worker committed, as it answers it: the object one block was printed into.</summary>
/// <param name="PartKey">The key the printed block was written under.</param>
/// <param name="Sha256">The SHA-256 of the stored object, lower-case hexadecimal.</param>
/// <param name="SizeBytes">The size of the stored object.</param>
/// <param name="PageCount">How many pages the printed block holds.</param>
public sealed record RenderPdfBlockStepResult(string PartKey, string Sha256, long SizeBytes, int PageCount);

/// <summary>The block render, as this worker schedules it on the PDF worker's queue.</summary>
public interface IRenderPdfBlockStep
{
    /// <summary>Prints one block of one output of one job.</summary>
    /// <param name="input">The job, the output and the block to print.</param>
    [Activity(PdfOutputStepContracts.RenderPdfBlock)]
    Task<RenderPdfBlockStepResult> RunAsync(RenderPdfBlockStepInput input);
}
