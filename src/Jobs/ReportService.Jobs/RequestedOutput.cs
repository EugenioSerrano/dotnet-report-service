namespace ReportService.Jobs;

/// <summary>
/// One output the service was asked to produce: the identifier its record will carry and the
/// format it is produced in. A job's acceptance carries one of these per distinct requested
/// format, in the order the request first named them, so the store writes one independent
/// output row per format without re-reading the request's tokens.
/// </summary>
/// <param name="OutputId">The identifier the output's record will have.</param>
/// <param name="Format">The format the output is produced in.</param>
public sealed record RequestedOutput(string OutputId, OutputFormat Format);
