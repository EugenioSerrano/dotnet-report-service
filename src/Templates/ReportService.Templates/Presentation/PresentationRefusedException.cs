namespace ReportService.Templates;

/// <summary>
/// What a presentation function raises when the value or the format it was given
/// cannot be presented: the stable code and reason of the refusal, the function and
/// the record it belongs to. It carries no value of the data and never the format's
/// expansion, and it is never retried - the same value and the same format reach the
/// same answer.
/// </summary>
public sealed class PresentationRefusedException : Exception
{
    /// <summary>Creates the refusal from the failure that describes it.</summary>
    /// <param name="failure">The code, the reason, the function and the record.</param>
    public PresentationRefusedException(PresentationFailure failure)
        : base(failure?.Message)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Failure = failure;
    }

    /// <summary>The refusal, as the block reports it.</summary>
    public PresentationFailure Failure { get; }
}
