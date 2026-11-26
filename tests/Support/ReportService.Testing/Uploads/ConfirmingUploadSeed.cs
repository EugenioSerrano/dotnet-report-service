using System.Security.Cryptography;
using ReportService.Storage;
using ReportService.Uploads;

namespace ReportService.Testing.Uploads;

/// <summary>
/// The confirming upload a multi-block case runs on, seeded and completed through
/// the product's own upload store and storage port. The seed writes the bytes where
/// the upload record names them, so the product's verifier reads the seeded object
/// and completes the upload on its own; the completion is the stand-in for that
/// verifier when the fixture reaches the upload first, and fixes its own write-once
/// snapshot copy. Both operations are the fixture's own, shared with the hermetic
/// cases, so the upload the cases run is seeded by exactly the code the cases are
/// checked against.
/// </summary>
public static class ConfirmingUploadSeed
{
    /// <summary>The attempt label the completion writes its snapshot copy under.</summary>
    public const string SnapshotAttempt = "a";

    /// <summary>The key an upload's bytes are written under: the product's staging class.</summary>
    /// <param name="uploadId">The identifier of the upload.</param>
    public static ObjectKey StagingKey(string uploadId) =>
        ObjectKey.FromSegments("staging", uploadId, "content");

    /// <summary>The write-once key the completion fixes its snapshot copy under.</summary>
    /// <param name="uploadId">The identifier of the upload.</param>
    public static ObjectKey SnapshotKey(string uploadId) =>
        ObjectKey.FromSegments("snapshots", uploadId, SnapshotAttempt);

    /// <summary>
    /// Creates the upload, starts its transfer, writes the bytes at the key the
    /// record names as the staging object, records the transfer with the written
    /// object's size and SHA-256, and confirms the upload — so the verifier may
    /// already complete it while the caller is still preparing the job.
    /// </summary>
    /// <param name="uploads">The upload records, as the product's own store writes them.</param>
    /// <param name="storage">The object storage port the composition shares.</param>
    /// <param name="consumerId">The declared consumer the upload belongs to.</param>
    /// <param name="snapshot">The bytes the upload is seeded with.</param>
    /// <param name="now">The instant the session's timestamps are taken from, in UTC.</param>
    /// <param name="cancellationToken">The case's cancellation token.</param>
    /// <returns>The upload and the keys the seed used, with the stored bytes' size and SHA-256.</returns>
    /// <exception cref="InvalidOperationException">A store step refused the seed, so no upload the cases can run on exists.</exception>
    public static async Task<SeededUpload> SeedAsync(
        IUploadStore uploads,
        IObjectStore storage,
        string consumerId,
        byte[] snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        ArgumentNullException.ThrowIfNull(snapshot);

        var uploadId = UploadIdentifier.Create();
        var stagingKey = StagingKey(uploadId);
        await uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                consumerId,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                stagingKey,
                now,
                now.AddHours(1)),
            cancellationToken);

        if (!await uploads.TryStartTransferAsync(uploadId, consumerId, UploadContentEncoding.None, now, cancellationToken))
        {
            throw new InvalidOperationException("the seeded upload could not start its transfer");
        }

        // The bytes are written where the record says they are, before the confirmation
        // can dispatch anything, and what landed is what the transfer records.
        using var body = new MemoryStream(snapshot, writable: false);
        var written = await storage.WriteAsync(stagingKey, body, cancellationToken);
        if (!written.Succeeded)
        {
            throw new InvalidOperationException(
                "the seeded upload's bytes could not be written: " + written.Outcome);
        }

        if (!await uploads.RecordTransferAsync(
                uploadId,
                UploadContentOutcome.Complete,
                snapshot.LongLength,
                written.Value!.Size,
                written.Value!.Sha256,
                now,
                cancellationToken))
        {
            throw new InvalidOperationException("the seeded upload could not record its transfer");
        }

        if (!await uploads.ConfirmAsync(uploadId, consumerId, null, now, now, now.AddHours(1), cancellationToken))
        {
            throw new InvalidOperationException("the seeded upload could not be confirmed");
        }

        return new SeededUpload(
            uploadId,
            stagingKey,
            SnapshotKey(uploadId),
            written.Value!.Size,
            written.Value!.Sha256);
    }

    /// <summary>
    /// Completes the seeded upload: a ready one is left as it is; a verifying one
    /// gets its own write-once snapshot copy, written and committed before the call,
    /// with the object's own size and SHA-256; and any other end state is reported
    /// unchanged, so the caller names it. A refused commit is re-read rather than
    /// assumed, because the composition's verifier may have completed the upload
    /// between the read and the commit.
    /// </summary>
    /// <param name="uploads">The upload records, as the product's own store writes them.</param>
    /// <param name="storage">The object storage port the composition shares.</param>
    /// <param name="seed">The upload and the keys the seed used.</param>
    /// <param name="consumerId">The declared consumer the upload belongs to.</param>
    /// <param name="snapshot">The bytes the upload was seeded with.</param>
    /// <param name="now">The instant the snapshot is fixed at, in UTC.</param>
    /// <param name="cancellationToken">The case's cancellation token.</param>
    /// <returns>The upload as it ended, ready or in the state that refused it.</returns>
    /// <exception cref="InvalidOperationException">The upload was not recorded, or a refused commit left it unusable.</exception>
    public static async Task<UploadRecord> CompleteAsync(
        IUploadStore uploads,
        IObjectStore storage,
        SeededUpload seed,
        string consumerId,
        byte[] snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        ArgumentNullException.ThrowIfNull(snapshot);

        var current = await uploads.FindAsync(seed.UploadId, consumerId, cancellationToken)
            ?? throw new InvalidOperationException("the upload was not recorded: " + seed.UploadId);

        switch (current.Status)
        {
            case UploadStatus.Ready:
                break;

            case UploadStatus.Verifying:
            {
                // The confirmation writes the stored bytes to a new write-once snapshot
                // key; this attempt's copy is written before the record that names it.
                using var body = new MemoryStream(snapshot, writable: false);
                var written = await storage.WriteAsync(seed.SnapshotKey, body, cancellationToken);
                if (!written.Succeeded)
                {
                    throw new InvalidOperationException(
                        "the seeded upload's snapshot copy could not be written: " + written.Outcome);
                }

                var contentSha256 = Convert.ToHexStringLower(SHA256.HashData(snapshot));
                var ready = await uploads.CommitReadyAsync(
                    new SnapshotRecord(
                        seed.UploadId,
                        consumerId,
                        seed.SnapshotKey,
                        UploadContentEncoding.None,
                        DataProfile.HeaderItemsV1,
                        contentSha256,
                        snapshot.LongLength,
                        written.Value!.Sha256,
                        written.Value!.Size,
                        now),
                    now,
                    cancellationToken);

                switch (ready)
                {
                    case SnapshotCommitResult.Committed:
                        await TryDeleteAsync(storage, seed.StagingKey, cancellationToken);
                        break;

                    case SnapshotCommitResult.AlreadyCommitted:
                    case SnapshotCommitResult.NotVerifying:
                        // The verifier may have finished between the read above and this
                        // commit: this attempt's object is not referenced, so it goes.
                        await TryDeleteAsync(storage, seed.SnapshotKey, cancellationToken);
                        var after = await uploads.FindAsync(seed.UploadId, consumerId, cancellationToken);
                        if (after?.Status != UploadStatus.Ready)
                        {
                            throw new InvalidOperationException(
                                "the upload could not be completed: " + ready
                                + ", status " + (after?.Status.ToString() ?? "missing"));
                        }

                        break;
                }

                break;
            }

            default:
                return current;
        }

        return await uploads.FindAsync(seed.UploadId, consumerId, cancellationToken)
            ?? throw new InvalidOperationException("the upload disappeared before it became ready: " + seed.UploadId);
    }

    private static async Task TryDeleteAsync(
        IObjectStore storage,
        ObjectKey key,
        CancellationToken cancellationToken)
    {
        // The staging object is deleted after the upload ends; an object that cannot be
        // removed now (an open read holds it) is left to the reader that owns it, and the
        // upload's outcome never depends on this removal.
        try
        {
            await storage.DeleteAsync(key, cancellationToken);
        }
        catch (StorageException)
        {
        }
    }
}

/// <summary>
/// An upload this fixture seeded: its identifier, the key its record names as the
/// staging object, the write-once key the confirmation writes its snapshot copy
/// under, and the stored bytes' size and SHA-256.
/// </summary>
/// <param name="UploadId">The identifier of the upload.</param>
/// <param name="StagingKey">The key the upload's record names as its bytes' object.</param>
/// <param name="SnapshotKey">The key the confirmation writes its snapshot copy under.</param>
/// <param name="SizeStored">The size of the stored bytes.</param>
/// <param name="Sha256Stored">The SHA-256 of the stored bytes, lower-case hexadecimal.</param>
public sealed record SeededUpload(
    string UploadId,
    ObjectKey StagingKey,
    ObjectKey SnapshotKey,
    long SizeStored,
    string Sha256Stored)
{
    /// <summary>The facts of an upload seeded earlier, from its identifier and bytes.</summary>
    /// <param name="uploadId">The identifier of the upload.</param>
    /// <param name="snapshot">The bytes the upload was seeded with.</param>
    public static SeededUpload FromSnapshot(string uploadId, byte[] snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        ArgumentNullException.ThrowIfNull(snapshot);
        return new SeededUpload(
            uploadId,
            ConfirmingUploadSeed.StagingKey(uploadId),
            ConfirmingUploadSeed.SnapshotKey(uploadId),
            snapshot.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(snapshot)));
    }
}
