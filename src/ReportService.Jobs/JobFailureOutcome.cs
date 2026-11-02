namespace ReportService.Jobs;

/// <summary>
/// What failing a job came to: the whole job was brought to its end, or it had
/// already ended and nothing changed. A step that arrives twice — or after another
/// step ended the job — gets the second answer and treats it as done.
/// </summary>
public enum JobFailureOutcome
{
    /// <summary>This call ended the job and every output of it that had not ended.</summary>
    Applied,

    /// <summary>The job had already ended; nothing was written.</summary>
    AlreadyTerminal,
}
