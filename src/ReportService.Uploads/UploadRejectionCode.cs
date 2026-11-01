namespace ReportService.Uploads;

/// <summary>
/// Why an upload ended rejected. The value never describes the consumer's data:
/// it is a stable code a caller can branch on, and the reason beside it says one
/// step further why.
/// </summary>
public enum UploadRejectionCode
{
    /// <summary>The content is not what the consumer declared, or is not whole.</summary>
    DataInvalid = 0,

    /// <summary>A size limit was crossed.</summary>
    QuotaExceeded = 1,

    /// <summary>The declared encoding or capability is not supported.</summary>
    UnsupportedCapability = 2,
}
