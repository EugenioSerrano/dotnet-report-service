using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReportService.Storage;
using ReportService.Uploads;

namespace ReportService.Api.Uploads;

/// <summary>
/// The step every upload operation runs after it has the consumer's own record:
/// when that record is past the end of its session, or past the verification
/// deadline of its confirmation, the operation expires it with one conditional
/// update and goes on with whatever the record now says. Expiring it here is what
/// makes the answer exact at the instant it is asked for, without waiting for the
/// background sweep; the sweep's own update is the same condition, so the two can
/// never expire an upload twice or disagree about when it ended.
/// </summary>
internal static class UploadExpiryStep
{
    /// <summary>The span tag that says this operation expired an upload.</summary>
    public const string ExpiredTag = "upload.expired";

    /// <summary>The span tag that carries why the upload expired.</summary>
    public const string ExpiryCauseTag = "upload.expiry_cause";

    /// <summary>Expires <paramref name="record"/> when <paramref name="now"/> finds it overdue.</summary>
    /// <param name="store">The upload records.</param>
    /// <param name="objects">The object storage port, for the staging object of an expired upload.</param>
    /// <param name="record">The upload the operation loaded.</param>
    /// <param name="now">The instant the operation is decided at, in UTC.</param>
    /// <param name="logger">The log of the API's upload requests.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>The upload as it stands afterwards, to answer by.</returns>
    public static async Task<UploadRecord> ApplyAsync(
        IUploadStore store,
        IObjectStore objects,
        UploadRecord record,
        DateTimeOffset now,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(logger);

        if (!UploadExpiry.IsOverdue(record, now))
        {
            return record;
        }

        var outcome = await store.ExpireIfOverdueAsync(record.UploadId, now, cancellationToken).ConfigureAwait(false);
        if (outcome.Upload is not { } current || !outcome.Expired)
        {
            // Another writer moved it first; the answer follows the state it left.
            return outcome.Upload ?? record;
        }

        var cause = UploadExpiry.CauseOf(record.Status);
        Activity.Current?.SetTag(ExpiredTag, true);
        Activity.Current?.SetTag(ExpiryCauseTag, cause);
        logger.LogInformation("Upload {UploadId} expired ({ExpiryCause})", current.UploadId, cause);
        await DeleteStagingAsync(objects, current.StagingKey, logger, cancellationToken).ConfigureAwait(false);
        return current;
    }

    // The bytes of an upload that has ended are of no further use; a failure here is
    // logged and left to the sweep and to the later cleanup, never answered to a caller.
    private static async Task DeleteStagingAsync(
        IObjectStore objects,
        ObjectKey key,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await objects.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            if (!deleted.Succeeded)
            {
                logger.LogWarning("A stored object could not be deleted ({StorageOutcome})", deleted.Outcome.ToString());
            }
        }
        catch (StorageException exception)
        {
            logger.LogWarning("A stored object could not be deleted ({StorageOutcome})", exception.Outcome.ToString());
        }
    }
}
