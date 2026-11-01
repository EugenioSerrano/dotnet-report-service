namespace ReportService.Storage;

/// <summary>
/// A storage failure raised where an outcome cannot be returned: while a
/// listing is enumerated, or while a verified read is consumed (see
/// <see cref="StorageIntegrityException"/>). Its message is the error's
/// message, which never contains a key, a path, a URL or a credential.
/// </summary>
public class StorageException : Exception
{
    /// <summary>Creates the exception for a failed outcome.</summary>
    public StorageException(StorageOutcome outcome, StorageError error)
        : base(error?.Message ?? throw new ArgumentNullException(nameof(error)), error.Inner)
    {
        if (outcome == StorageOutcome.Success)
        {
            throw new ArgumentException("A storage exception needs an outcome other than success.", nameof(outcome));
        }

        Outcome = outcome;
        Error = error;
    }

    /// <summary>The outcome of the failed operation.</summary>
    public StorageOutcome Outcome { get; }

    /// <summary>The error of the failed operation.</summary>
    public StorageError Error { get; }
}
