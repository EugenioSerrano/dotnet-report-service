using System.Globalization;

namespace ReportService.Workers.Orchestration.Uploads;

/// <summary>
/// Raised as the decompressed count would cross the configured limit: the count
/// stops at the limit and the upload is refused. The message carries the limit
/// and never a content byte.
/// </summary>
public sealed class UncompressedQuotaExceededException : Exception
{
    /// <summary>Creates the exception for <paramref name="limit"/>.</summary>
    /// <param name="limit">The decompressed-size limit the content crossed.</param>
    public UncompressedQuotaExceededException(long limit)
        : base(string.Create(
            CultureInfo.InvariantCulture,
            $"The content decompresses past the limit of {limit} bytes.")) =>
        Limit = limit;

    /// <summary>The decompressed-size limit the content crossed.</summary>
    public long Limit { get; }
}
