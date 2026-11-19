namespace ReportService.Templates;

/// <summary>
/// A block whose package cannot be composed because of its engine: the version declares no
/// engine, the deployment offers none for it, the pin and the taken package disagree, or the
/// declared engine version's major is not the one the adapter runs. It is raised before
/// anything is composed, and it carries a code, a reason and engine names - never a value of
/// the data - so the block ends with the code the service's catalogue gives it.
/// </summary>
public sealed class EngineRefusedException : Exception
{
    /// <summary>Creates the refusal from the decision that refused the block.</summary>
    /// <param name="decision">The refusal: its code, its reason and its one-line diagnostic.</param>
    public EngineRefusedException(EngineDecision decision)
        : base(decision?.Message)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Resolved)
        {
            throw new ArgumentException("The decision resolved an adapter and refuses nothing.", nameof(decision));
        }

        Code = decision.Code ?? EngineResolution.TemplateInvalid;
        Reason = decision.Reason ?? EngineResolution.EngineUndeclared;
    }

    /// <summary>The error code the block ends with.</summary>
    public string Code { get; }

    /// <summary>The stable reason within that code.</summary>
    public string Reason { get; }
}
