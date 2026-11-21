using ReportService.Jobs;
using ReportService.Storage;

namespace ReportService.Api.Artifacts;

/// <summary>
/// The one place an artifact's stored object is opened: the key, the length and
/// the SHA-256 all come from the row the owner's read authorised, never from the
/// request, so a caller cannot name an object at all. The answer is the port's
/// own outcome — success, a missing object, a transient cause or a permanent one
/// — and never a provider exception or a path. The content is a stream: no
/// download is ever held whole.
/// </summary>
public sealed class ArtifactContent(IObjectStore objects)
{
    /// <summary>Opens the stored object of <paramref name="artifact"/> for a verified read.</summary>
    /// <param name="artifact">The artifact row its owner's read returned.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>The verifying reader, or the port's failure outcome.</returns>
    public Task<StorageResult<VerifiedRead>> OpenAsync(ArtifactView artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        ObjectKey key;
        try
        {
            key = ObjectKey.Create(artifact.ObjectKey);
        }
        catch (InvalidObjectKeyException exception)
        {
            // A row whose stored key the port refuses is one the service cannot read: it is
            // answered as a stored object it cannot reach, never as a fault of the request,
            // and the refused text never leaves this method.
            return Task.FromResult(StorageResult.Failure<VerifiedRead>(
                StorageOutcome.Permanent,
                new StorageError(StorageErrorCodes.Unexpected, "the stored object key is not a key", exception)));
        }

        return objects.OpenReadAsync(key, artifact.SizeBytes, artifact.Sha256, cancellationToken);
    }
}
