using System.Globalization;

namespace ReportService.Jobs;

/// <summary>
/// The text forms of a job's values. The record store keeps each enumeration in
/// its domain text and parses it back strictly, so a value it cannot read is a
/// corrupted row and never a default; an answer to a consumer carries the same
/// words in lower case. Every mapping is an exhaustive switch that refuses an
/// unmapped value.
/// </summary>
public static class JobWireValues
{
    /// <summary>The answer text of a job that was accepted and not started yet.</summary>
    public const string QueuedWire = "queued";

    /// <summary>The answer text of <paramref name="status"/>.</summary>
    /// <param name="status">The status to translate.</param>
    public static string ToWire(ReportJobStatus status) => status switch
    {
        ReportJobStatus.Queued => QueuedWire,
        ReportJobStatus.Validating => "validating",
        ReportJobStatus.Partitioning => "partitioning",
        ReportJobStatus.Running => "running",
        ReportJobStatus.Finalizing => "finalizing",
        ReportJobStatus.Succeeded => "succeeded",
        ReportJobStatus.Failed => "failed",
        ReportJobStatus.Cancelling => "cancelling",
        ReportJobStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), "the job status has no answer text"),
    };

    /// <summary>The text the record store keeps for <paramref name="status"/>.</summary>
    /// <param name="status">The status to translate.</param>
    public static string ToStoredText(ReportJobStatus status) => status switch
    {
        ReportJobStatus.Queued => "QUEUED",
        ReportJobStatus.Validating => "VALIDATING",
        ReportJobStatus.Partitioning => "PARTITIONING",
        ReportJobStatus.Running => "RUNNING",
        ReportJobStatus.Finalizing => "FINALIZING",
        ReportJobStatus.Succeeded => "SUCCEEDED",
        ReportJobStatus.Failed => "FAILED",
        ReportJobStatus.Cancelling => "CANCELLING",
        ReportJobStatus.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), "the job status has no stored text"),
    };

    /// <summary>The text the record store keeps for <paramref name="status"/>.</summary>
    /// <param name="status">The output status to translate.</param>
    public static string ToStoredText(OutputStatus status) => status switch
    {
        OutputStatus.Pending => "PENDING",
        OutputStatus.Running => "RUNNING",
        OutputStatus.Succeeded => "SUCCEEDED",
        OutputStatus.Failed => "FAILED",
        OutputStatus.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), "the output status has no stored text"),
    };

    /// <summary>The text the record store keeps for <paramref name="format"/>.</summary>
    /// <param name="format">The format to translate.</param>
    public static string ToStoredText(OutputFormat format) => format switch
    {
        OutputFormat.Pdf => "PDF",
        OutputFormat.Html => "HTML",
        OutputFormat.Xlsx => "XLSX",
        OutputFormat.Markdown => "MARKDOWN",
        _ => throw new ArgumentOutOfRangeException(nameof(format), "the output format has no stored text"),
    };

    /// <summary>The text the record store keeps for <paramref name="form"/>.</summary>
    /// <param name="form">The reference form to translate.</param>
    public static string ToStoredText(TemplateReferenceForm form) => form switch
    {
        TemplateReferenceForm.Exact => "EXACT",
        TemplateReferenceForm.ByName => "BY_NAME",
        _ => throw new ArgumentOutOfRangeException(nameof(form), "the reference form has no stored text"),
    };

    /// <summary>Reads the stored text of a job status; an unknown text is refused, never defaulted.</summary>
    /// <param name="value">The text the store returned.</param>
    public static ReportJobStatus ParseStoredStatus(string value) => value switch
    {
        "QUEUED" => ReportJobStatus.Queued,
        "VALIDATING" => ReportJobStatus.Validating,
        "PARTITIONING" => ReportJobStatus.Partitioning,
        "RUNNING" => ReportJobStatus.Running,
        "FINALIZING" => ReportJobStatus.Finalizing,
        "SUCCEEDED" => ReportJobStatus.Succeeded,
        "FAILED" => ReportJobStatus.Failed,
        "CANCELLING" => ReportJobStatus.Cancelling,
        "CANCELLED" => ReportJobStatus.Cancelled,
        _ => throw new InvalidOperationException("the stored job status is not a known value"),
    };

    /// <summary>Reads the stored text of an output status; an unknown text is refused, never defaulted.</summary>
    /// <param name="value">The text the store returned.</param>
    public static OutputStatus ParseStoredOutputStatus(string value) => value switch
    {
        "PENDING" => OutputStatus.Pending,
        "RUNNING" => OutputStatus.Running,
        "SUCCEEDED" => OutputStatus.Succeeded,
        "FAILED" => OutputStatus.Failed,
        "CANCELLED" => OutputStatus.Cancelled,
        _ => throw new InvalidOperationException("the stored output status is not a known value"),
    };

    /// <summary>Reads the stored text of an output format; an unknown text is refused, never defaulted.</summary>
    /// <param name="value">The text the store returned.</param>
    public static OutputFormat ParseStoredFormat(string value) => value switch
    {
        "PDF" => OutputFormat.Pdf,
        "HTML" => OutputFormat.Html,
        "XLSX" => OutputFormat.Xlsx,
        "MARKDOWN" => OutputFormat.Markdown,
        _ => throw new InvalidOperationException("the stored output format is not a known value"),
    };

    /// <summary>Reads the stored text of a reference form; an unknown text is refused, never defaulted.</summary>
    /// <param name="value">The text the store returned.</param>
    public static TemplateReferenceForm ParseStoredForm(string value) => value switch
    {
        "EXACT" => TemplateReferenceForm.Exact,
        "BY_NAME" => TemplateReferenceForm.ByName,
        _ => throw new InvalidOperationException("the stored reference form is not a known value"),
    };

    /// <summary>Truncates <paramref name="value"/> to whole milliseconds in UTC, the precision every row carries.</summary>
    /// <param name="value">The value to truncate.</param>
    public static DateTimeOffset TruncateToMillisecond(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }
}
