using ReportService.TemplatePackages.Records;

namespace ReportService.TemplateSource.Take;

/// <summary>
/// What one take answered. Three of the four states carry the record the version
/// is usable through — the take produced it, met it already, or logged that the
/// source changed under it and bound the copy that stays — and the fourth is the
/// take's own refusal, with the code the caller branches on, the text an author
/// has to act on and whether taking the same version again may succeed.
/// </summary>
public abstract record TemplateTakeOutcome
{
    private protected TemplateTakeOutcome()
    {
    }

    /// <summary>The take read the version, kept it and recorded it.</summary>
    /// <param name="record">The record it wrote.</param>
    public static TemplateTakeOutcome Taken(TemplateVersionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new TakenVersion(record);
    }

    /// <summary>The version was already taken and this caller may use it: the source was not read.</summary>
    /// <param name="record">The record that already existed.</param>
    public static TemplateTakeOutcome AlreadyTaken(TemplateVersionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new AlreadyTakenVersion(record);
    }

    /// <summary>
    /// The version was taken by another take while this one read it, and the
    /// bytes differ: the difference was logged and the taken copy is what stays.
    /// </summary>
    /// <param name="record">The record that stays.</param>
    public static TemplateTakeOutcome ConflictLogged(TemplateVersionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new ConflictLoggedVersion(record);
    }

    /// <summary>The take refused the version.</summary>
    /// <param name="code">One of <see cref="TemplateTakeCodes"/>.</param>
    /// <param name="reason">What the author or the operator has to act on.</param>
    /// <param name="retryable">Whether taking the same version again later may succeed.</param>
    public static TemplateTakeOutcome Refused(string code, string reason, bool retryable)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new RefusedVersion(code, reason, retryable);
    }

    /// <summary>The record the answer carries, or <see langword="null"/> when the take refused.</summary>
    public TemplateVersionRecord? Record => (this as VersionOutcome)?.Version;

    /// <summary>What the answer is called where it is logged and counted.</summary>
    public abstract string Name { get; }
}

/// <summary>An answer that carries the record of the version.</summary>
/// <param name="Version">The record.</param>
public abstract record VersionOutcome(TemplateVersionRecord Version) : TemplateTakeOutcome;

/// <summary>The take wrote the record.</summary>
/// <param name="Version">The record it wrote.</param>
public sealed record TakenVersion(TemplateVersionRecord Version) : VersionOutcome(Version)
{
    /// <inheritdoc />
    public override string Name => "taken";
}

/// <summary>The version was already taken, and this caller may use it.</summary>
/// <param name="Version">The record that existed.</param>
public sealed record AlreadyTakenVersion(TemplateVersionRecord Version) : VersionOutcome(Version)
{
    /// <inheritdoc />
    public override string Name => "already_taken";
}

/// <summary>The version was taken by another take whose bytes differ; the copy stays.</summary>
/// <param name="Version">The record that stays.</param>
public sealed record ConflictLoggedVersion(TemplateVersionRecord Version) : VersionOutcome(Version)
{
    /// <inheritdoc />
    public override string Name => "conflict_logged";
}

/// <summary>The take refused the version.</summary>
/// <param name="Code">One of <see cref="TemplateTakeCodes"/>.</param>
/// <param name="Reason">What the author or the operator has to act on.</param>
/// <param name="Retryable">Whether taking the same version again later may succeed.</param>
public sealed record RefusedVersion(string Code, string Reason, bool Retryable) : TemplateTakeOutcome
{
    /// <inheritdoc />
    public override string Name => "refused";
}
