namespace ReportService.Workers.Merge.Assembly;

/// <summary>What assembling an output is asked for: the job and the output it produces.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="OutputId">The identifier of the PDF output of the job.</param>
public sealed record AssemblePdfInput(string ReportJobId, string OutputId);

/// <summary>What the assembly wrote: the one output-class object and the values publication records.</summary>
/// <param name="ObjectKey">The attempt-scoped key the verified document was written under.</param>
/// <param name="Sha256">The SHA-256 of the stored object, lower-case hexadecimal.</param>
/// <param name="SizeBytes">The size of the stored object.</param>
/// <param name="PageCount">How many pages the assembled document holds.</param>
public sealed record AssemblePdfResult(string ObjectKey, string Sha256, long SizeBytes, int PageCount);
