namespace ReportService.Jobs;

/// <summary>
/// The step a job enters when it moves to a state. It is written together with the
/// state in the same statement, so the step can never go back while the state cannot:
/// the two advance in the same act. A state no step of this increment enters has no
/// stage, and asking for one is a programming error rather than a default.
/// </summary>
public static class JobStages
{
    /// <summary>The step <paramref name="to"/> enters the job into.</summary>
    /// <param name="to">The state the job is moved to.</param>
    /// <exception cref="ArgumentOutOfRangeException">No stage of this increment is entered by that state.</exception>
    public static ReportJobStage StageFor(ReportJobStatus to) => to switch
    {
        ReportJobStatus.Validating => ReportJobStage.ValidatingData,
        ReportJobStatus.Running => ReportJobStage.RenderingPdf,
        _ => throw new ArgumentOutOfRangeException(nameof(to), "the job status enters no stage of this increment"),
    };
}
