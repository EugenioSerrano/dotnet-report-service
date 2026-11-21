namespace ReportService.Jobs;

/// <summary>
/// How a job named its template: a single version, or a name whose version is
/// decided when the job takes its copy. The form is stored as written at
/// acceptance, so the step that resolves the template knows what was asked for.
/// </summary>
public enum TemplateReferenceForm
{
    /// <summary>A named version: the request carried one and it is stored beside the form.</summary>
    Exact,

    /// <summary>A name only: the version is decided by the step that takes the copy.</summary>
    ByName,
}
