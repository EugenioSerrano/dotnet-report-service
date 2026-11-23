namespace ReportService.Api.Uploads;

/// <summary>
/// Raised by the body reader when one more block would carry the transfer past
/// the size limit the operation enforces. It carries no length and no byte: the
/// caller knows the limit and reads the count from the reader itself.
/// </summary>
public sealed class UploadQuotaExceededException : Exception
{
    /// <summary>Creates the exception.</summary>
    public UploadQuotaExceededException()
        : base("the request body crossed the size limit of this operation.")
    {
    }
}
