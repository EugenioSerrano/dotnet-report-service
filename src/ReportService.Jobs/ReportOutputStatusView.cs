namespace ReportService.Jobs;

/// <summary>
/// One output of a job as its owner reads it: the form it is produced in, its own
/// state, the code it failed with and — once it was published — the identifier of
/// the file it produced. The identifier is visible only for a succeeded output,
/// because only then does the artifact exist in the same record.
/// </summary>
/// <param name="Format">The form the output is produced in.</param>
/// <param name="Status">The state of the output.</param>
/// <param name="ErrorCode">The stable code the output failed with; null unless it failed.</param>
/// <param name="ArtifactId">The identifier of the published file; null unless the output succeeded.</param>
public sealed record ReportOutputStatusView(
    OutputFormat Format,
    OutputStatus Status,
    string? ErrorCode,
    string? ArtifactId);
