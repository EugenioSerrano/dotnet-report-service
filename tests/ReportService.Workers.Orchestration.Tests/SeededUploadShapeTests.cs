using System.Security.Cryptography;
using ReportService.Storage;
using ReportService.Testing.Uploads;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Uploads;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The upload a multi-block case is seeded with, checked where it stands: the
/// object the record names holds the seeded bytes; the composition's verifier
/// completes it when it reaches it first; the fixture's own completion fixes a
/// snapshot it wrote when it reaches it first; a completion after the verifier
/// changes nothing; and a record whose object does not exist is still refused and
/// reported, never completed silently. The five cases need no container and no
/// clock: they run the verifier over the in-memory records and the filesystem
/// store of one temporary root.
/// </summary>
public sealed class SeededUploadShapeTests
{
    [Fact]
    public async Task TheSeededUploadsRecordedStagingKeyHoldsTheSnapshotBytes()
    {
        using var rig = new VerifierRig();
        var snapshot = Payload();
        var seed = await SeedAsync(rig, snapshot);

        var record = await rig.Uploads.FindAsync(
            seed.UploadId,
            VerifierRig.ConsumerId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(record);
        Assert.Equal(seed.StagingKey.ToString(), record.StagingKey.ToString());
        Assert.Equal(seed.SizeStored, record.StagingSize!.Value);
        Assert.Equal(seed.Sha256Stored, record.StagingSha256);

        var stored = await ReadAsync(rig, record.StagingKey, record.StagingSize!.Value, record.StagingSha256!, TestContext.Current.CancellationToken);
        Assert.Equal(snapshot, stored);
    }

    [Fact]
    public async Task TheVerifierCompletesTheSeededUploadWhenItReachesItFirst()
    {
        using var rig = new VerifierRig();
        var snapshot = Payload();
        var seed = await SeedAsync(rig, snapshot);
        var attempt = VerifierRig.AttemptKey(seed.UploadId);

        var outcome = await rig.Verifier.VerifyAsync(
            seed.UploadId,
            attempt,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationResult.Ready, outcome.Result);
        var committed = Assert.IsType<SnapshotRecord>(outcome.Snapshot);
        Assert.Equal(attempt.ToString(), committed.ObjectKey.ToString());
        var ready = Assert.IsType<UploadRecord>(await rig.Uploads.LoadForVerificationAsync(
            seed.UploadId,
            TestContext.Current.CancellationToken));
        Assert.Equal(UploadStatus.Ready, ready.Status);
        Assert.NotNull(ready.Snapshot);
        Assert.Equal(attempt.ToString(), ready.Snapshot!.ObjectKey.ToString());

        var stored = await ReadAsync(rig, committed.ObjectKey, committed.SizeStored, committed.Sha256Stored, TestContext.Current.CancellationToken);
        Assert.Equal(snapshot, stored);
        Assert.Empty(await rig.ListAsync($"staging/{seed.UploadId}"));
    }

    [Fact]
    public async Task TheFixtureCompletionCommitsTheSnapshotItWroteWhenItReachesItFirst()
    {
        using var rig = new VerifierRig();
        var snapshot = Payload();
        var seed = await SeedAsync(rig, snapshot);

        var completed = await CompleteAsync(rig, seed, snapshot);

        Assert.Equal(UploadStatus.Ready, completed.Status);
        var ready = Assert.IsType<UploadRecord>(await rig.Uploads.LoadForVerificationAsync(
            seed.UploadId,
            TestContext.Current.CancellationToken));
        var committed = Assert.IsType<SnapshotRecord>(ready.Snapshot);
        Assert.Equal(seed.SnapshotKey.ToString(), committed.ObjectKey.ToString());

        var stored = await ReadAsync(rig, committed.ObjectKey, committed.SizeStored, committed.Sha256Stored, TestContext.Current.CancellationToken);
        Assert.Equal(snapshot, stored);
        Assert.Empty(await rig.ListAsync($"staging/{seed.UploadId}"));

        var writes = rig.Objects.Writes;
        var again = await CompleteAsync(rig, seed, snapshot);
        Assert.Equal(UploadStatus.Ready, again.Status);
        Assert.Equal(writes, rig.Objects.Writes);
    }

    [Fact]
    public async Task TheFixtureCompletionChangesNothingWhenTheVerifierAlreadyCompleted()
    {
        using var rig = new VerifierRig();
        var snapshot = Payload();
        var seed = await SeedAsync(rig, snapshot);
        var attempt = VerifierRig.AttemptKey(seed.UploadId);
        var outcome = await rig.Verifier.VerifyAsync(
            seed.UploadId,
            attempt,
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(VerificationResult.Ready, outcome.Result);

        var writes = rig.Objects.Writes;
        var completed = await CompleteAsync(rig, seed, snapshot);

        Assert.Equal(UploadStatus.Ready, completed.Status);
        Assert.Equal(writes, rig.Objects.Writes);
        var snapshotKeys = await rig.ListAsync($"snapshots/{seed.UploadId}");
        Assert.Equal([attempt.ToString()], snapshotKeys);
        var ready = Assert.IsType<UploadRecord>(await rig.Uploads.LoadForVerificationAsync(
            seed.UploadId,
            TestContext.Current.CancellationToken));
        Assert.Equal(attempt.ToString(), Assert.IsType<SnapshotRecord>(ready.Snapshot).ObjectKey.ToString());
    }

    [Fact]
    public async Task TheFixtureStillReportsADivergenceItCannotComplete()
    {
        using var rig = new VerifierRig();
        var snapshot = Payload();
        var seed = await SeedAsync(rig, snapshot);
        var deleted = await rig.Objects.DeleteAsync(seed.StagingKey, TestContext.Current.CancellationToken);
        Assert.True(deleted.Succeeded, deleted.Outcome.ToString());

        var outcome = await rig.Verifier.VerifyAsync(
            seed.UploadId,
            VerifierRig.AttemptKey(seed.UploadId),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationResult.Rejected, outcome.Result);
        Assert.Equal(UploadRejectionCode.DataInvalid, outcome.Code);
        Assert.Equal(UploadRejectionReason.Modified, outcome.Reason);

        var writes = rig.Objects.Writes;
        var completed = await CompleteAsync(rig, seed, snapshot);

        Assert.Equal(UploadStatus.Rejected, completed.Status);
        Assert.Equal(writes, rig.Objects.Writes);
        Assert.Null(rig.Uploads.SnapshotOf(seed.UploadId));
    }

    private static byte[] Payload()
    {
        // The bytes are the fixture's concern, not the verifier's: a small fixed body
        // keeps the cases fast while still exercising the size and hash checks.
        var payload = new byte[4096];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)('a' + (index % 26));
        }

        return payload;
    }

    private static Task<SeededUpload> SeedAsync(VerifierRig rig, byte[] snapshot) =>
        ConfirmingUploadSeed.SeedAsync(
            rig.Uploads,
            rig.Objects,
            VerifierRig.ConsumerId,
            snapshot,
            rig.Clock.GetUtcNow(),
            TestContext.Current.CancellationToken);

    private static Task<UploadRecord> CompleteAsync(VerifierRig rig, SeededUpload seed, byte[] snapshot) =>
        ConfirmingUploadSeed.CompleteAsync(
            rig.Uploads,
            rig.Objects,
            seed,
            VerifierRig.ConsumerId,
            snapshot,
            rig.Clock.GetUtcNow(),
            TestContext.Current.CancellationToken);

    private static async Task<byte[]> ReadAsync(
        VerifierRig rig,
        ObjectKey key,
        long size,
        string sha256,
        CancellationToken cancellationToken)
    {
        var opened = await rig.Objects.OpenReadAsync(key, size, sha256, cancellationToken);
        Assert.True(opened.Succeeded, opened.Outcome.ToString());
        await using var verified = opened.Value!;
        using var read = new MemoryStream();
        await verified.Content.CopyToAsync(read, cancellationToken);
        return read.ToArray();
    }
}
