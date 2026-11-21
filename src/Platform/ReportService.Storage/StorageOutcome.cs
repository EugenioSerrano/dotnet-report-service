namespace ReportService.Storage;

/// <summary>The outcome of a storage operation. Only <see cref="Success"/> carries a value.</summary>
public enum StorageOutcome
{
    /// <summary>The operation completed.</summary>
    Success,

    /// <summary>No object exists at the key.</summary>
    NotFound,

    /// <summary>An object already exists at the key; nothing was written and the existing object is unchanged.</summary>
    AlreadyExists,

    /// <summary>A failure that may succeed when retried later (for example an input/output error).</summary>
    Transient,

    /// <summary>A failure that retrying will not fix (configuration, permission, quota, an unsafe path).</summary>
    Permanent,

    /// <summary>The content does not match its expected size or SHA-256.</summary>
    IntegrityFailure,
}
