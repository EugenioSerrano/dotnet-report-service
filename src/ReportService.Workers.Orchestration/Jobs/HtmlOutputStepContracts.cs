using Temporalio.Activities;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The one activity the orchestration worker serves for a job's HTML output: assembling
/// the composed document-mode HTML into one self-contained <c>.html</c>. It is scheduled
/// by name on <c>report-html</c> — distinct from the PDF worker's Chromium queue
/// <c>report-pdf-rendering</c> (AC-4) — and never starts a browser. The records below are
/// the shape the HTML output workflow schedules and reads.
/// </summary>
public static class HtmlOutputStepContracts
{
    /// <summary>The name the HTML assembly is registered and scheduled under.</summary>
    public const string AssembleHtml = "AssembleHtml";
}

/// <summary>What assembling one job's HTML output is asked for.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="OutputId">The identifier of the job's HTML output.</param>
public sealed record AssembleHtmlStepInput(string ReportJobId, string OutputId);

/// <summary>The object the assembly committed: the one <c>.html</c> it wrote, with no page count.</summary>
/// <param name="ObjectKey">The key the assembled file was written under.</param>
/// <param name="Sha256">The SHA-256 of the stored object, lower-case hexadecimal.</param>
/// <param name="SizeBytes">The size of the stored object.</param>
public sealed record AssembleHtmlStepResult(string ObjectKey, string Sha256, long SizeBytes);

/// <summary>The HTML assembly, as the HTML output workflow schedules it on <c>report-html</c>.</summary>
public interface IAssembleHtmlStep
{
    /// <summary>Assembles the one HTML output of one job.</summary>
    /// <param name="input">The job and the output.</param>
    [Activity(HtmlOutputStepContracts.AssembleHtml)]
    Task<AssembleHtmlStepResult> RunAsync(AssembleHtmlStepInput input);
}
