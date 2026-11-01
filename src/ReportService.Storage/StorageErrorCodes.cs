namespace ReportService.Storage;

/// <summary>The stable error codes of the storage port.</summary>
public static class StorageErrorCodes
{
    /// <summary>An object already exists at the key.</summary>
    public const string AlreadyExists = "storage.already-exists";

    /// <summary>No object exists at the key.</summary>
    public const string NotFound = "storage.not-found";

    /// <summary>The key resolves outside the storage root, directly or through a link.</summary>
    public const string PathOutsideRoot = "storage.path-outside-root";

    /// <summary>The key resolves to a path longer than the provider allows.</summary>
    public const string PathTooLong = "storage.path-too-long";

    /// <summary>The key names a directory, not an object.</summary>
    public const string KeyIsDirectory = "storage.key-is-directory";

    /// <summary>A leading part of the key names an object, so the key cannot hold one.</summary>
    public const string KeyConflict = "storage.key-conflict";

    /// <summary>The provider is not allowed to perform the operation.</summary>
    public const string PermissionDenied = "storage.permission-denied";

    /// <summary>An input/output error that may not happen again.</summary>
    public const string IoError = "storage.io-error";

    /// <summary>The store cannot publish an object without risking an overwrite.</summary>
    public const string CreateOnlyUnsupported = "storage.create-only-unsupported";

    /// <summary>The content read is not as long as expected.</summary>
    public const string SizeMismatch = "storage.integrity.size-mismatch";

    /// <summary>The content read has the expected size but another SHA-256.</summary>
    public const string HashMismatch = "storage.integrity.hash-mismatch";

    /// <summary>The operation did not finish within its time bound.</summary>
    public const string Timeout = "storage.timeout";

    /// <summary>An unexpected failure.</summary>
    public const string Unexpected = "storage.unexpected";
}
