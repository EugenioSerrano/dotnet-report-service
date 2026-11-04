namespace ReportService.Jobs;

/// <summary>
/// The long-running step a job is in while it runs. The values are ordered by rank:
/// a job only ever moves to a step at or after the one it carries, and a terminal
/// job is in none. The record store admits every value of this enumeration so a
/// later step can write its stage without a new script.
/// </summary>
public enum ReportJobStage
{
    /// <summary>The job waits for the upload it reports on to finish being verified.</summary>
    WaitingForInput,

    /// <summary>The job's template reference is being resolved.</summary>
    Resolving,

    /// <summary>The job's input is being checked against its data profile.</summary>
    ValidatingData,

    /// <summary>The input is being split into the units of work that are printed.</summary>
    Partitioning,

    /// <summary>The report's HTML documents are being composed.</summary>
    ComposingHtml,

    /// <summary>The report's pages are being rendered to PDF.</summary>
    RenderingPdf,

    /// <summary>The rendered parts are being merged into one document.</summary>
    MergingPdf,

    /// <summary>The report is being exported as a spreadsheet.</summary>
    ExportingXlsx,

    /// <summary>The report is being converted to Markdown.</summary>
    ConvertingMarkdown,

    /// <summary>The report's file is being published.</summary>
    Publishing,

    /// <summary>The job's consumer is being notified.</summary>
    Notifying,
}
