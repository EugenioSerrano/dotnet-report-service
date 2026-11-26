using Temporalio.Activities;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The orchestration worker's view of the merge worker's assembly step: its name
/// and the records it reads and answers. The records mirror the delivered ones
/// member for member - same names, same types - so the answer the merge worker
/// writes is read here exactly as it was written. The integration suite compares
/// both sides; a drift is a stop, never a silent retry.
/// </summary>
public static class AssemblePdfStepContracts
{
    /// <summary>The name of the assembly, as the merge worker registers it.</summary>
    public const string AssemblePdf = "AssemblePdf";
}

/// <summary>What assembling one output is asked for, as the merge worker reads it.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="OutputId">The identifier of the PDF output of the job.</param>
public sealed record AssemblePdfStepInput(string ReportJobId, string OutputId);

/// <summary>What the assembly wrote, as the merge worker answers it.</summary>
/// <param name="ObjectKey">The attempt-scoped key the verified document was written under.</param>
/// <param name="Sha256">The SHA-256 of the stored object, lower-case hexadecimal.</param>
/// <param name="SizeBytes">The size of the stored object.</param>
/// <param name="PageCount">How many pages the assembled document holds.</param>
public sealed record AssemblePdfStepResult(string ObjectKey, string Sha256, long SizeBytes, int PageCount);

/// <summary>The assembly, as this worker schedules it on the merge worker's queue.</summary>
public interface IAssemblePdfStep
{
    /// <summary>Assembles one output of one job.</summary>
    /// <param name="input">The job and the output.</param>
    [Activity(AssemblePdfStepContracts.AssemblePdf)]
    Task<AssemblePdfStepResult> RunAsync(AssemblePdfStepInput input);
}
