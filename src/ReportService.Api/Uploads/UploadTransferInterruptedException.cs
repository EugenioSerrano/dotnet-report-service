namespace ReportService.Api.Uploads;

/// <summary>
/// Raised by the body reader when the transfer ended before the body did: the
/// connection failed, the request was aborted, or the bytes that arrived are
/// fewer than the length the caller declared. It carries no reason text from the
/// transport, which can name a host or a socket.
/// </summary>
public sealed class UploadTransferInterruptedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public UploadTransferInterruptedException()
        : base("the request body ended before its declared end.")
    {
    }

    /// <summary>Creates the exception over the transport failure it was translated from.</summary>
    /// <param name="inner">The failure the transport raised.</param>
    public UploadTransferInterruptedException(Exception inner)
        : base("the request body ended before its declared end.", inner)
    {
    }
}
