namespace ReportService.Api.Uploads;

/// <summary>
/// The record store could not be reached or did not answer in time. The message
/// names no host, user, statement or database: the cause is carried as the inner
/// exception for the process's own operator, never for a caller.
/// </summary>
public sealed class UploadStoreUnavailableException : Exception
{
    /// <summary>Creates the exception with the state the server reported, when it reported one.</summary>
    /// <param name="sqlState">The server's state code, or <see langword="null"/> when the server never answered.</param>
    /// <param name="innerException">The cause, kept for the process's own operators.</param>
    public UploadStoreUnavailableException(string? sqlState, Exception innerException)
        : base("The upload record store is not reachable.", innerException) =>
        SqlState = sqlState;

    /// <summary>The server's state code, or <see langword="null"/> when the server never answered.</summary>
    public string? SqlState { get; }
}
