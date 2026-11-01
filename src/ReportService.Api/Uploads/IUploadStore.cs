namespace ReportService.Api.Uploads;

/// <summary>
/// The upload records of the service. Every read takes the declared consumer as
/// well as the identifier, so no caller can read a session without saying whose
/// it is looking for; an upload of another consumer is answered exactly as a
/// session that does not exist.
/// </summary>
public interface IUploadStore
{
    /// <summary>Records a new session.</summary>
    /// <param name="record">The session to record.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    Task CreateAsync(UploadRecord record, CancellationToken cancellationToken);

    /// <summary>Finds a session by its identifier, among those of <paramref name="consumerId"/>.</summary>
    /// <param name="uploadId">The identifier of the session.</param>
    /// <param name="consumerId">The declared consumer asking.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>The session, or <see langword="null"/> when it does not exist for that consumer.</returns>
    Task<UploadRecord?> FindAsync(string uploadId, string consumerId, CancellationToken cancellationToken);
}
