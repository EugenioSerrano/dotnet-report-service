using System.Globalization;

namespace ReportService.Storage;

/// <summary>
/// Raised by a verified read when the content consumed does not have the
/// expected size or SHA-256 — a shorter, longer or changed object. It carries
/// the expected and actual figures, never a key or a path.
/// </summary>
public sealed class StorageIntegrityException : StorageException
{
    /// <summary>Creates the exception from the expected and actual figures.</summary>
    public StorageIntegrityException(long expectedSize, long actualSize, string expectedSha256, string actualSha256)
        : base(StorageOutcome.IntegrityFailure, Describe(expectedSize, actualSize, expectedSha256, actualSha256))
    {
        ExpectedSize = expectedSize;
        ActualSize = actualSize;
        ExpectedSha256 = expectedSha256;
        ActualSha256 = actualSha256;
    }

    /// <summary>The size the caller expected, in bytes.</summary>
    public long ExpectedSize { get; }

    /// <summary>The number of bytes read.</summary>
    public long ActualSize { get; }

    /// <summary>The SHA-256 the caller expected, lower-case hexadecimal.</summary>
    public string ExpectedSha256 { get; }

    /// <summary>The SHA-256 of the bytes read, lower-case hexadecimal.</summary>
    public string ActualSha256 { get; }

    private static StorageError Describe(long expectedSize, long actualSize, string expectedSha256, string actualSha256) =>
        expectedSize != actualSize
            ? new StorageError(
                StorageErrorCodes.SizeMismatch,
                string.Create(CultureInfo.InvariantCulture, $"The object has {actualSize} bytes; {expectedSize} were expected."))
            : new StorageError(
                StorageErrorCodes.HashMismatch,
                $"The object's SHA-256 is {actualSha256}; {expectedSha256} was expected.");
}
