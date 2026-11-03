namespace ReportService.TemplatePackages.Records;

/// <summary>
/// The taken template versions of the service. A version becomes usable by
/// being recorded, once: the store never updates and never deletes a record,
/// and an insert that meets an existing record answers that record instead of
/// replacing it.
/// </summary>
public interface ITemplateVersionStore
{
    /// <summary>Finds the record of one version.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The record, or <see langword="null"/> when the version was never taken.</returns>
    Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken);

    /// <summary>The records of every version of one template that was ever taken.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>Every record of the template, ordered by the version string in byte order.</returns>
    Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a version, unless an identical identity is already recorded.
    /// </summary>
    /// <param name="record">The version to record.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>What the store now holds: the inserted record, or the one that was already there.</returns>
    Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken);
}

/// <summary>What an insert left the store holding.</summary>
public sealed record TemplateVersionInsert
{
    private TemplateVersionInsert(bool inserted, TemplateVersionRecord record)
    {
        WasInserted = inserted;
        Record = record;
    }

    /// <summary>True when this call recorded the version; false when it was already recorded.</summary>
    public bool WasInserted { get; }

    /// <summary>The record the store holds.</summary>
    public TemplateVersionRecord Record { get; }

    /// <summary>This call recorded <paramref name="record"/>.</summary>
    /// <param name="record">The record that was written.</param>
    public static TemplateVersionInsert Inserted(TemplateVersionRecord record) => new(true, record);

    /// <summary>The version was already recorded, and <paramref name="record"/> is what the store holds.</summary>
    /// <param name="record">The record that was already there.</param>
    public static TemplateVersionInsert Existing(TemplateVersionRecord record) => new(false, record);
}

/// <summary>
/// The store could not be reached or did not answer. The message names the
/// operation and no connection detail, and the cause is kept for the log.
/// </summary>
public sealed class TemplateStoreUnavailableException : Exception
{
    /// <summary>The store did not answer.</summary>
    public TemplateStoreUnavailableException()
        : base("the template version store is unavailable")
    {
    }

    /// <summary>The store did not answer; <paramref name="innerException"/> says why.</summary>
    /// <param name="innerException">The cause, for the log.</param>
    public TemplateStoreUnavailableException(Exception innerException)
        : base("the template version store is unavailable", innerException)
    {
    }

    /// <summary>The store did not answer; <paramref name="message"/> names the operation.</summary>
    /// <param name="message">What was being attempted.</param>
    /// <param name="innerException">The cause, for the log.</param>
    public TemplateStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
