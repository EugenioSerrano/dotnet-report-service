namespace ReportService.Jobs;

/// <summary>
/// One cause a rendering attempt recorded before it ended, keyed by the output, the
/// block and the attempt - never by a run, because a continuation of the output's
/// execution changes the run and never the attempt's meaning. The cause is one of the
/// two deadline classes of <see cref="AttemptCauseVocabulary"/>; a row carries no value,
/// no message, no path and no key.
/// </summary>
/// <param name="OutputId">The identifier of the output the attempt was printing.</param>
/// <param name="BlockIndex">The block's position in the output's partition, from 0.</param>
/// <param name="Attempt">The attempt number the engine reported, from 1.</param>
/// <param name="Cause">The deadline class the attempt ended with.</param>
/// <param name="RecordedAt">When the cause was recorded, in UTC.</param>
public sealed record AttemptCause(
    string OutputId,
    int BlockIndex,
    int Attempt,
    string Cause,
    DateTimeOffset RecordedAt);

/// <summary>What recording one attempt's cause did.</summary>
public enum AttemptCauseOutcome
{
    /// <summary>This call wrote the row.</summary>
    Recorded,

    /// <summary>The attempt's row existed already; nothing was written and the first row stays.</summary>
    AlreadyRecorded,
}
