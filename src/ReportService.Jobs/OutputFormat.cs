namespace ReportService.Jobs;

/// <summary>
/// The form a job can ask its report in. Only <see cref="Pdf"/> is offered today;
/// the other values exist so a stored row of a later increment is read and never
/// silently taken for a supported one.
/// </summary>
public enum OutputFormat
{
    /// <summary>A PDF document.</summary>
    Pdf,

    /// <summary>An HTML document, not offered yet.</summary>
    Html,

    /// <summary>A spreadsheet, not offered yet.</summary>
    Xlsx,

    /// <summary>Markdown text, not offered yet.</summary>
    Markdown,
}
