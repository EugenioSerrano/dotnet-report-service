namespace ReportService.Jobs;

/// <summary>
/// The stage a job records while it waits for the upload it reports on to finish
/// being verified. It is a step of the job's own long-running work, not a state of
/// the job: the job stays queued for the whole wait.
/// </summary>
public static class InputWaitStage
{
    /// <summary>The step of a job that is accepted and has not started rendering yet.</summary>
    public const string WaitingForInput = "WAITING_FOR_INPUT";
}

/// <summary>
/// What recording a job's waiting stage came to: the stage was written, or the job
/// was not queued any more — it had already ended, or it does not exist. Both are
/// ordinary answers of a conditional write; neither is a failure.
/// </summary>
public enum BeginInputWaitOutcome
{
    /// <summary>The job is queued and now carries the waiting stage.</summary>
    Applied,

    /// <summary>The job had already ended, or it does not exist; nothing was written.</summary>
    NotQueued,
}

/// <summary>
/// What clearing a job's waiting stage came to: the stage was cleared, or the job was
/// not waiting any more. A repeated clear answers the second, which is a no-op.
/// </summary>
public enum EndInputWaitOutcome
{
    /// <summary>The job was waiting and its stage is cleared.</summary>
    Applied,

    /// <summary>The job was not in the waiting stage — or a previous clear already took it; nothing changed.</summary>
    NotWaiting,
}
