using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ReportService.Storage;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Telemetry;
using ReportService.Workers.Orchestration.Uploads;
using ReportService.Workers.Orchestration.Tests.Uploads;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The verification of one upload, from the stored bytes to the committed
/// outcome: what ends ready, what is refused and with which code and reason,
/// that a refused upload leaves neither a snapshot record nor a snapshot object,
/// that the stored object is read once, and that nothing of the consumer's data
/// reaches a log.
/// </summary>
public sealed class UploadVerifierTests
{
    private const string UploadId = "upload-3f2b0c9e8d7a4b6c9e1f0a2b3c4d5e6f";

    [Fact]
    public async Task APlainBodyEndsReadyWithTheSameHashAndSizeStored()
    {
        using var rig = new VerifierRig();
        var content = GzipMember.Content(4096);
        await rig.GivenVerifyingAsync(UploadId, content, UploadContentEncoding.None);

        var outcome = await VerifyAsync(rig);

        Assert.Equal(VerificationResult.Ready, outcome.Result);
        var snapshot = Assert.IsType<SnapshotRecord>(outcome.Snapshot);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), snapshot.Sha256Uncompressed);
        Assert.Equal(content.Length, snapshot.SizeUncompressed);
        Assert.Equal(snapshot.Sha256Uncompressed, snapshot.Sha256Stored);
        Assert.Equal(content.Length, snapshot.SizeStored);
        Assert.Equal(UploadContentEncoding.None, snapshot.Compression);
        Assert.Equal(UploadStatus.Ready, Assert.Single(rig.Uploads.Records).Status);
        Assert.Equal(UploadStatus.Ready, (await rig.Uploads.LoadForVerificationAsync(UploadId, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task ACompressedBodyEndsReadyWithTheContentHashAndSizeAndTheStoredHashApart()
    {
        using var rig = new VerifierRig();
        var content = GzipMember.Content(8192);
        var stored = GzipMember.Compress(content);
        await rig.GivenVerifyingAsync(UploadId, stored, UploadContentEncoding.Gzip);

        var outcome = await VerifyAsync(rig);

        Assert.Equal(VerificationResult.Ready, outcome.Result);
        var snapshot = Assert.IsType<SnapshotRecord>(outcome.Snapshot);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), snapshot.Sha256Uncompressed);
        Assert.Equal(content.Length, snapshot.SizeUncompressed);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(stored)), snapshot.Sha256Stored);
        Assert.Equal(stored.Length, snapshot.SizeStored);
        Assert.NotEqual(snapshot.Sha256Stored, snapshot.Sha256Uncompressed);
        Assert.Equal(UploadContentEncoding.Gzip, snapshot.Compression);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADeclaredHashThatMatchesEndsReady(bool lowerCase)
    {
        using var rig = new VerifierRig();
        var content = GzipMember.Content(2048);
        var stored = GzipMember.Compress(content);
        var declared = Convert.ToHexStringLower(SHA256.HashData(content));
        await rig.GivenVerifyingAsync(
            UploadId,
            stored,
            UploadContentEncoding.Gzip,
            declaredSha256: lowerCase ? declared : declared.ToUpperInvariant());

        var outcome = await VerifyAsync(rig);

        Assert.Equal(VerificationResult.Ready, outcome.Result);
        Assert.Equal(declared, outcome.Snapshot!.Sha256Uncompressed);
    }

    [Fact]
    public async Task ADeclaredHashThatDiffersIsRejectedAndNothingIsFixed()
    {
        using var rig = new VerifierRig();
        var content = GzipMember.Content(2048);
        var stored = GzipMember.Compress(content);
        await rig.GivenVerifyingAsync(
            UploadId,
            stored,
            UploadContentEncoding.Gzip,
            declaredSha256: new string('a', 64));

        var outcome = await VerifyAsync(rig);

        Assert.Equal(VerificationResult.Rejected, outcome.Result);
        Assert.Equal(UploadRejectionCode.DataInvalid, outcome.Code);
        Assert.Equal(UploadRejectionReason.Sha256Mismatch, outcome.Reason);
        await AssertNothingFixedAsync(rig);
    }

    [Fact]
    public async Task AnInterruptedTransferIsRejectedWithoutReadingAnything()
    {
        using var rig = new VerifierRig();
        await rig.GivenVerifyingAsync(
            UploadId,
            GzipMember.Compress(GzipMember.Content(1024)),
            UploadContentEncoding.Gzip,
            outcome: UploadContentOutcome.Interrupted,
            writeObject: false,
            stagingRecorded: false);

        var outcome = await VerifyAsync(rig);

        Assert.Equal(UploadRejectionReason.Truncated, outcome.Reason);
        Assert.Equal(UploadRejectionCode.DataInvalid, outcome.Code);
        Assert.Equal(0, rig.Objects.Opens);
        await AssertNothingFixedAsync(rig);
    }

    [Fact]
    public async Task AnEncodingTheServiceDoesNotDecodeIsRejected()
    {
        using var rig = new VerifierRig();
        await rig.GivenVerifyingAsync(UploadId, GzipMember.Compress(GzipMember.Content(1024)), UploadContentEncoding.Other);

        var outcome = await VerifyAsync(rig);

        Assert.Equal(UploadRejectionCode.UnsupportedCapability, outcome.Code);
        Assert.Equal(UploadRejectionReason.Encoding, outcome.Reason);
        Assert.Equal(0, rig.Objects.Opens);
        await AssertNothingFixedAsync(rig);
    }

    [Fact]
    public async Task AFlippedByteInTheCompressedStreamIsRejectedAsMalformed()
    {
        using var rig = new VerifierRig();
        var stored = GzipMember.Flip(GzipMember.Compress(GzipMember.Content(4096)), 200);
        await rig.GivenVerifyingAsync(UploadId, stored, UploadContentEncoding.Gzip);

        var outcome = await VerifyAsync(rig);

        Assert.Equal(UploadRejectionCode.DataInvalid, outcome.Code);
        Assert.Equal(UploadRejectionReason.Malformed, outcome.Reason);
        await AssertNothingFixedAsync(rig);
    }

    [Fact]
    public async Task AFlippedLengthInTheTrailerIsRejectedAsMalformed()
    {
        using var rig = new VerifierRig();
        var stored = GzipMember.Flip(GzipMember.Compress(GzipMember.Content(4096)), 0);
        await rig.GivenVerifyingAsync(UploadId, stored, UploadContentEncoding.Gzip);

        var outcome = await VerifyAsync(rig);

        Assert.Equal(UploadRejectionCode.DataInvalid, outcome.Code);
        Assert.Equal(UploadRejectionReason.Malformed, outcome.Reason);
        await AssertNothingFixedAsync(rig);
    }

    [Fact]
    public async Task AFlippedChecksumInTheTrailerIsRejectedAsMalformed()
    {
        using var rig = new VerifierRig();
        var stored = GzipMember.Flip(GzipMember.Compress(GzipMember.Content(4096)), 4);
        await rig.GivenVerifyingAsync(UploadId, stored, UploadContentEncoding.Gzip);

        var outcome = await VerifyAsync(rig);

        Assert.Equal(UploadRejectionCode.DataInvalid, outcome.Code);
        Assert.Equal(UploadRejectionReason.Malformed, outcome.Reason);
        await AssertNothingFixedAsync(rig);
    }

    [Fact]
    public async Task ContentThatDecompressesPastTheLimitIsRejectedAtTheLimit()
    {
        // 64 KiB of a single repeated byte compresses to a few hundred bytes and expands past 16 KiB.
        using var rig = new VerifierRig(maxUncompressedBytes: 16 * 1024);
        var bomb = new byte[64 * 1024];
        Array.Fill(bomb, (byte)'a');
        var stored = GzipMember.Compress(bomb, System.IO.Compression.CompressionLevel.SmallestSize);
        await rig.GivenVerifyingAsync(UploadId, stored, UploadContentEncoding.Gzip);

        var outcome = await VerifyAsync(rig);

        Assert.Equal(UploadRejectionCode.QuotaExceeded, outcome.Code);
        Assert.Equal(UploadRejectionReason.InputBytes, outcome.Reason);
        await AssertNothingFixedAsync(rig);
    }

    [Fact]
    public async Task AnAlteredStagingObjectIsRejectedAsModified()
    {
        using var rig = new VerifierRig();
        var stored = GzipMember.Compress(GzipMember.Content(4096));
        await rig.GivenVerifyingAsync(UploadId, stored, UploadContentEncoding.Gzip);

        // The object is replaced after the transfer recorded what arrived.
        var tampered = GzipMember.Compress(GzipMember.Content(4096, "tampered"));
        var key = ObjectKeyFrom(UploadId);
        await rig.Objects.DeleteAsync(key, TestContext.Current.CancellationToken);
        using (var replace = new MemoryStream(tampered, writable: false))
        {
            await rig.Objects.WriteAsync(key, replace, TestContext.Current.CancellationToken);
        }

        var outcome = await VerifyAsync(rig);

        Assert.Equal(UploadRejectionCode.DataInvalid, outcome.Code);
        Assert.Equal(UploadRejectionReason.Modified, outcome.Reason);
        await AssertNothingFixedAsync(rig);
    }

    [Fact]
    public async Task AMissingStagingObjectIsRejectedAsModified()
    {
        using var rig = new VerifierRig();
        var stored = GzipMember.Compress(GzipMember.Content(1024));
        await rig.GivenVerifyingAsync(UploadId, stored, UploadContentEncoding.Gzip);
        await rig.Objects.DeleteAsync(ObjectKeyFrom(UploadId), TestContext.Current.CancellationToken);

        var outcome = await VerifyAsync(rig);

        Assert.Equal(UploadRejectionReason.Modified, outcome.Reason);
        await AssertNothingFixedAsync(rig);
    }

    [Fact]
    public async Task AStoredObjectOverTheLimitIsRejectedBeforeItIsRead()
    {
        using var rig = new VerifierRig(maxStoredBytes: 64);
        var stored = GzipMember.Compress(GzipMember.Content(4096));
        await rig.GivenVerifyingAsync(UploadId, stored, UploadContentEncoding.Gzip);

        var outcome = await VerifyAsync(rig);

        Assert.Equal(UploadRejectionCode.QuotaExceeded, outcome.Code);
        Assert.Equal(UploadRejectionReason.StoredBytes, outcome.Reason);
        Assert.Equal(0, rig.Objects.Opens);
        await AssertNothingFixedAsync(rig);
    }

    [Fact]
    public async Task EveryCutOfACompressedBodyIsRejectedAndEveryReasonIsRecorded()
    {
        using var rig = new VerifierRig();
        var stored = GzipMember.Compress(GzipMember.Content(300));
        var reasons = new Dictionary<UploadRejectionReason, int>();

        for (var cut = 0; cut < stored.Length - 1; cut++)
        {
            var uploadId = $"upload-{cut:x8}{new string('0', 24)}";
            await rig.GivenVerifyingAsync(
                uploadId,
                stored[..(cut + 1)],
                UploadContentEncoding.Gzip,
                stagingRecorded: true);

            var outcome = await VerifyAsync(rig, uploadId);

            Assert.Equal(VerificationResult.Rejected, outcome.Result);
            Assert.Equal(UploadRejectionCode.DataInvalid, outcome.Code);
            Assert.NotNull(outcome.Reason);
            reasons[outcome.Reason!.Value] = reasons.GetValueOrDefault(outcome.Reason!.Value) + 1;
            await AssertNothingFixedAsync(rig, uploadId);
        }

        // The measured distribution: a cut of a complete object ends silently, so it is
        // refused for a trailer that does not attest; a corrupt present member is malformed.
        Assert.Contains(UploadRejectionReason.Truncated, reasons.Keys);
        Assert.Equal(stored.Length - 1, reasons.Values.Sum());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    public async Task AnObjectTooShortToBeOneUnitOfContentIsRejected(int length)
    {
        using var rig = new VerifierRig();
        var stored = GzipMember.Compress(GzipMember.Content(300))[..length];
        await rig.GivenVerifyingAsync(UploadId, stored, UploadContentEncoding.Gzip, stagingRecorded: true);

        var outcome = await VerifyAsync(rig);

        Assert.Equal(UploadRejectionCode.DataInvalid, outcome.Code);
        Assert.Equal(UploadRejectionReason.Truncated, outcome.Reason);
        await AssertNothingFixedAsync(rig);
    }

    [Fact]
    public async Task TheSmallestPossibleMemberIsAcceptedWithNoContent()
    {
        using var rig = new VerifierRig();
        await rig.GivenVerifyingAsync(UploadId, GzipMember.Empty(), UploadContentEncoding.Gzip);

        var outcome = await VerifyAsync(rig);

        Assert.Equal(VerificationResult.Ready, outcome.Result);
        Assert.Equal(0, outcome.Snapshot!.SizeUncompressed);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([])), outcome.Snapshot.Sha256Uncompressed);
    }

    [Fact]
    public async Task EveryHeaderFlagVariantAndBothCompressionLevelsAreAccepted()
    {
        using var rig = new VerifierRig();
        var content = GzipMember.Content(2048);
        var forms = new Dictionary<string, byte[]>
        {
            ["no flag"] = GzipMember.Build(content),
            ["extra field"] = GzipMember.Build(content, flags: 0x04, extra: [1, 2, 3, 4]),
            ["name"] = GzipMember.Build(content, flags: 0x08, name: "report.json"),
            ["comment"] = GzipMember.Build(content, flags: 0x10, comment: "generated"),
            ["header checksum"] = GzipMember.Build(content, flags: 0x02),
            ["all of them"] = GzipMember.Build(content, flags: 0x1e, extra: [9, 9], name: "n.json", comment: "c"),
            ["no compression"] = GzipMember.Compress(content, System.IO.Compression.CompressionLevel.NoCompression),
            ["most compression"] = GzipMember.Compress(content, System.IO.Compression.CompressionLevel.SmallestSize),
        };

        var index = 0;
        foreach (var form in forms)
        {
            var uploadId = $"upload-{index++:x8}{new string('a', 24)}";
            await rig.GivenVerifyingAsync(uploadId, form.Value, UploadContentEncoding.Gzip);

            var outcome = await VerifyAsync(rig, uploadId);

            Assert.Equal(VerificationResult.Ready, outcome.Result);
            Assert.Equal(content.Length, outcome.Snapshot!.SizeUncompressed);
        }
    }

    [Fact]
    public async Task AConcatenationOfTwoMembersIsRejectedAndTheCornerWithAnEmptyFirstIsAccepted()
    {
        using var rig = new VerifierRig();
        var first = GzipMember.Build(GzipMember.Content(200, "first"));
        var second = GzipMember.Build(GzipMember.Content(300, "second"));

        await rig.GivenVerifyingAsync(UploadId, [.. first, .. second], UploadContentEncoding.Gzip);
        var refused = await VerifyAsync(rig);
        Assert.Equal(UploadRejectionCode.DataInvalid, refused.Code);
        Assert.Equal(UploadRejectionReason.Truncated, refused.Reason);
        await AssertNothingFixedAsync(rig);

        // Every member except the last decompresses to nothing, so the final trailer
        // attests exactly the content that is fixed.
        const string CornerId = "upload-11111111111111111111111111111111";
        await rig.GivenVerifyingAsync(CornerId, [.. GzipMember.Empty(), .. second], UploadContentEncoding.Gzip);
        var accepted = await VerifyAsync(rig, CornerId);

        Assert.Equal(VerificationResult.Ready, accepted.Result);
        Assert.Equal(300, accepted.Snapshot!.SizeUncompressed);

        const string ChainId = "upload-22222222222222222222222222222222";
        await rig.GivenVerifyingAsync(ChainId, [.. GzipMember.Empty(), .. GzipMember.Empty()], UploadContentEncoding.Gzip);
        var chain = await VerifyAsync(rig, ChainId);
        Assert.Equal(VerificationResult.Ready, chain.Result);
        Assert.Equal(0, chain.Snapshot!.SizeUncompressed);
    }

    [Fact]
    public async Task ACompressedBodyCutAtItsTrailerIsRefusedEvenWithACorrectDeclaredHash()
    {
        using var rig = new VerifierRig();
        var content = GzipMember.Content(4096);
        var stored = GzipMember.Compress(content);
        var cut = stored[..^4];
        await rig.GivenVerifyingAsync(
            UploadId,
            cut,
            UploadContentEncoding.Gzip,
            declaredSha256: Convert.ToHexStringLower(SHA256.HashData(content)));

        var outcome = await VerifyAsync(rig);

        Assert.Equal(UploadRejectionCode.DataInvalid, outcome.Code);
        Assert.Equal(UploadRejectionReason.Truncated, outcome.Reason);
        await AssertNothingFixedAsync(rig);
    }

    [Fact]
    public async Task TheStoredObjectIsReadOnce()
    {
        using var rig = new VerifierRig();
        var stored = GzipMember.Compress(GzipMember.Content(4096));
        await rig.GivenVerifyingAsync(UploadId, stored, UploadContentEncoding.Gzip);

        var outcome = await VerifyAsync(rig);

        Assert.Equal(VerificationResult.Ready, outcome.Result);
        Assert.Equal(1, rig.Objects.Opens);

        // One read, and the outcome is the conclusion of a read that had ended on both
        // sides: a trailer cut to the same length is refused, which can only be decided
        // once the whole object has been read and its content measured.
        using var second = new VerifierRig();
        await second.GivenVerifyingAsync(UploadId, stored[..^1], UploadContentEncoding.Gzip);
        var refused = await VerifyAsync(second);
        Assert.Equal(1, second.Objects.Opens);
        Assert.True(refused.Result == VerificationResult.Rejected);
    }

    [Fact]
    public async Task ARejectionCarriesNoContentByteKeyLengthOrPath()
    {
        using var rig = new VerifierRig();
        const string Marker = "planted-content-marker";
        var content = GzipMember.Content(512, Marker);
        var stored = GzipMember.Compress(content);
        stored = stored[..^8];
        await rig.GivenVerifyingAsync(UploadId, stored, UploadContentEncoding.Gzip);

        var outcome = await VerifyAsync(rig);
        var logs = string.Join('\n', rig.Logs.Lines);

        Assert.Equal(VerificationResult.Rejected, outcome.Result);
        Assert.DoesNotContain(Marker, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rig.Root, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshots/", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("staging/", logs, StringComparison.Ordinal);
        Assert.Null(outcome.Snapshot);
    }

    [Fact]
    public async Task TheSnapshotObjectIsDeletedFromStagingAndReadsBackAsTheStoredBytes()
    {
        using var rig = new VerifierRig();
        var stored = GzipMember.Compress(GzipMember.Content(5000));
        await rig.GivenVerifyingAsync(UploadId, stored, UploadContentEncoding.Gzip);

        var outcome = await VerifyAsync(rig);

        var snapshot = Assert.IsType<SnapshotRecord>(outcome.Snapshot);
        Assert.Empty(await rig.ListAsync($"staging/{UploadId}"));

        var opened = await rig.Objects.OpenReadAsync(
            snapshot.ObjectKey,
            snapshot.SizeStored,
            snapshot.Sha256Stored,
            TestContext.Current.CancellationToken);
        Assert.True(opened.Succeeded);
        using var read = opened.Value!;
        using var buffer = new MemoryStream();
        await read.Content.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        Assert.Equal(stored, buffer.ToArray());
    }

    [Fact]
    public async Task ARejectedUploadLeavesNoSnapshotObjectAndNoStagingObject()
    {
        using var rig = new VerifierRig();
        var cut = GzipMember.Compress(GzipMember.Content(5000))[..^8];
        await rig.GivenVerifyingAsync(UploadId, cut, UploadContentEncoding.Gzip);

        await VerifyAsync(rig);

        Assert.Empty(await rig.ListAsync($"snapshots/{UploadId}"));
        Assert.Empty(await rig.ListAsync($"staging/{UploadId}"));
    }

    [Fact]
    public async Task ASecondRunAfterReadyReportsTheRecordedOutcomeAndWritesNothing()
    {
        using var rig = new VerifierRig();
        await rig.GivenVerifyingAsync(UploadId, GzipMember.Compress(GzipMember.Content(1024)), UploadContentEncoding.Gzip);
        var first = await VerifyAsync(rig);
        var writes = rig.Objects.Writes;
        var opens = rig.Objects.Opens;

        var second = await VerifyAsync(rig);

        Assert.Equal(VerificationResult.Ready, second.Result);
        Assert.Equal(first.Snapshot, second.Snapshot);
        Assert.Equal(writes, rig.Objects.Writes);
        Assert.Equal(opens, rig.Objects.Opens);
    }

    [Fact]
    public async Task AMissingUploadIsNotFound()
    {
        using var rig = new VerifierRig();

        var outcome = await VerifyAsync(rig);

        Assert.Equal(VerificationResult.NotFound, outcome.Result);
        Assert.Equal(0, rig.Objects.Opens);
    }

    [Fact]
    public async Task AnUploadThatIsNotWaitingToBeVerifiedWritesNothing()
    {
        using var rig = new VerifierRig();
        await rig.GivenVerifyingAsync(UploadId, GzipMember.Compress(GzipMember.Content(1024)), UploadContentEncoding.Gzip, confirm: false);
        var writes = rig.Objects.Writes;

        var outcome = await VerifyAsync(rig);

        Assert.Equal(VerificationResult.NotVerifying, outcome.Result);
        Assert.Equal(writes, rig.Objects.Writes);
    }

    [Fact]
    public async Task StorageThatFailsIsThrownSoTheAttemptIsRetried()
    {
        using var rig = new VerifierRig();
        await rig.GivenVerifyingAsync(UploadId, GzipMember.Compress(GzipMember.Content(1024)), UploadContentEncoding.Gzip);
        rig.Objects.ReadFailure = StorageOutcome.Transient;

        await Assert.ThrowsAsync<StorageException>(() => VerifyAsync(rig));
    }

    [Fact]
    public async Task AFailureTheStoredSideRecordedWinsOverAStoreThatReportedSuccess()
    {
        using var rig = new VerifierRig();
        var stored = GzipMember.Compress(GzipMember.Content(4096));
        await rig.GivenVerifyingAsync(UploadId, stored, UploadContentEncoding.Gzip);
        rig.Objects.ReportWriteSuccessOnSourceFailure = true;

        // The staging object no longer matches what was recorded, so the read fails while
        // the port still reports that it wrote something.
        await rig.Objects.DeleteAsync(ObjectKeyFrom(UploadId), TestContext.Current.CancellationToken);
        using (var tampered = new MemoryStream(stored[..^2], writable: false))
        {
            await rig.Objects.WriteAsync(ObjectKeyFrom(UploadId), tampered, TestContext.Current.CancellationToken);
        }

        var outcome = await VerifyAsync(rig);

        Assert.Equal(UploadRejectionReason.Modified, outcome.Reason);
        Assert.Contains(VerifierRig.AttemptKey(UploadId, "attempt").ToString(), rig.Objects.Deleted);
        await AssertNothingFixedAsync(rig);
    }

    [Fact]
    public async Task AnEarlierSnapshotStandsAndThisAttemptsObjectIsDeleted()
    {
        using var rig = new VerifierRig();
        var stored = GzipMember.Compress(GzipMember.Content(1024));
        await rig.GivenVerifyingAsync(UploadId, stored, UploadContentEncoding.Gzip);
        var winner = new SnapshotRecord(
            UploadId,
            VerifierRig.ConsumerId,
            VerifierRig.AttemptKey(UploadId, "winner"),
            UploadContentEncoding.Gzip,
            DataProfile.HeaderItemsV1,
            Convert.ToHexStringLower(SHA256.HashData(GzipMember.Content(1024))),
            1024,
            Convert.ToHexStringLower(SHA256.HashData(stored)),
            stored.Length,
            new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero));
        var scripted = new ScriptedUploadStore(rig.Uploads)
        {
            ReadyResult = SnapshotCommitResult.AlreadyCommitted,
            Winner = winner,
        };
        var verifier = rig.VerifierOver(scripted);

        var outcome = await verifier.VerifyAsync(
            UploadId,
            VerifierRig.AttemptKey(UploadId),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationResult.Ready, outcome.Result);
        Assert.Equal(winner.ObjectKey, outcome.Snapshot!.ObjectKey);
        Assert.Contains(VerifierRig.AttemptKey(UploadId, "attempt").ToString(), rig.Objects.Deleted);
    }

    [Fact]
    public async Task AnUploadThatStoppedVerifyingBeforeTheCommitKeepsNoSnapshot()
    {
        using var rig = new VerifierRig();
        await rig.GivenVerifyingAsync(UploadId, GzipMember.Compress(GzipMember.Content(1024)), UploadContentEncoding.Gzip);
        var scripted = new ScriptedUploadStore(rig.Uploads) { ReadyResult = SnapshotCommitResult.NotVerifying };
        var verifier = rig.VerifierOver(scripted);

        var outcome = await verifier.VerifyAsync(
            UploadId,
            VerifierRig.AttemptKey(UploadId),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationResult.NotVerifying, outcome.Result);
        Assert.Contains(VerifierRig.AttemptKey(UploadId, "attempt").ToString(), rig.Objects.Deleted);
    }

    [Fact]
    public async Task ProgressIsNeverReportedTwiceWithinASecond()
    {
        using var rig = new VerifierRig();
        var content = new byte[512 * 1024];
        Array.Fill(content, (byte)'x');
        await rig.GivenVerifyingAsync(UploadId, GzipMember.Compress(content), UploadContentEncoding.Gzip);
        var reports = new List<long>();

        await rig.Verifier.VerifyAsync(
            UploadId,
            VerifierRig.AttemptKey(UploadId),
            reports.Add,
            TestContext.Current.CancellationToken);

        // The clock never moves, so nothing is due inside one second.
        Assert.Empty(reports);
    }

    [Fact]
    public async Task ProgressIsReportedAsTheStoredBytesArrive()
    {
        using var rig = new VerifierRig(ticking: true);
        var content = new byte[512 * 1024];
        Array.Fill(content, (byte)'y');
        await rig.GivenVerifyingAsync(UploadId, GzipMember.Compress(content), UploadContentEncoding.Gzip);
        var reports = new List<long>();

        var outcome = await rig.Verifier.VerifyAsync(
            UploadId,
            VerifierRig.AttemptKey(UploadId),
            reports.Add,
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationResult.Ready, outcome.Result);
        Assert.NotEmpty(reports);
        Assert.Equal(reports.Order(), reports);
        Assert.True(reports[^1] <= outcome.Snapshot!.SizeStored);
    }

    private static ObjectKey ObjectKeyFrom(string uploadId) =>
        ObjectKey.FromSegments("staging", uploadId, "content");

    private static Task<VerificationOutcome> VerifyAsync(VerifierRig rig, string? uploadId = null) =>
        rig.Verifier.VerifyAsync(
            uploadId ?? UploadId,
            VerifierRig.AttemptKey(uploadId ?? UploadId),
            null,
            TestContext.Current.CancellationToken);

    private static async Task AssertNothingFixedAsync(VerifierRig rig, string? uploadId = null)
    {
        var id = uploadId ?? UploadId;
        Assert.Null(rig.Uploads.SnapshotOf(id));
        Assert.Empty(await rig.ListAsync($"snapshots/{id}"));
    }
}

/// <summary>
/// The upload records, with the outcome of a snapshot commit chosen by the test:
/// the case where an earlier attempt committed first, and the case where the
/// upload stopped waiting before this one could commit.
/// </summary>
internal sealed class ScriptedUploadStore : IUploadStore
{
    private readonly IUploadStore _inner;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    /// <param name="inner">The store that does the work.</param>
    public ScriptedUploadStore(IUploadStore inner) => _inner = inner;

    /// <summary>What a commit reports, instead of what the store would decide.</summary>
    public SnapshotCommitResult? ReadyResult { get; set; }

    /// <summary>The snapshot an earlier attempt committed, when the test wants one.</summary>
    public SnapshotRecord? Winner { get; set; }

    /// <inheritdoc />
    public Task CreateAsync(UploadRecord record, CancellationToken cancellationToken) =>
        _inner.CreateAsync(record, cancellationToken);

    /// <inheritdoc />
    public Task<UploadRecord?> FindAsync(string uploadId, string consumerId, CancellationToken cancellationToken) =>
        _inner.FindAsync(uploadId, consumerId, cancellationToken);

    /// <inheritdoc />
    public Task<bool> TryStartTransferAsync(string uploadId, string consumerId, UploadContentEncoding encoding, DateTimeOffset now, CancellationToken cancellationToken) =>
        _inner.TryStartTransferAsync(uploadId, consumerId, encoding, now, cancellationToken);

    /// <inheritdoc />
    public Task<bool> RecordTransferAsync(string uploadId, UploadContentOutcome outcome, long receivedBytes, long? stagingSize, string? stagingSha256, DateTimeOffset now, CancellationToken cancellationToken) =>
        _inner.RecordTransferAsync(uploadId, outcome, receivedBytes, stagingSize, stagingSha256, now, cancellationToken);

    /// <inheritdoc />
    public Task<bool> RejectTransferAsync(string uploadId, UploadRejectionCode code, UploadRejectionReason reason, long receivedBytes, DateTimeOffset now, CancellationToken cancellationToken) =>
        _inner.RejectTransferAsync(uploadId, code, reason, receivedBytes, now, cancellationToken);

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string uploadId, string consumerId, string? declaredSha256, DateTimeOffset completedAt, DateTimeOffset now, DateTimeOffset verificationDeadline, CancellationToken cancellationToken) =>
        _inner.ConfirmAsync(uploadId, consumerId, declaredSha256, completedAt, now, verificationDeadline, cancellationToken);

    /// <inheritdoc />
    public Task<UploadExpiryOutcome> ExpireIfOverdueAsync(string uploadId, DateTimeOffset now, CancellationToken cancellationToken) =>
        _inner.ExpireIfOverdueAsync(uploadId, now, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ExpiredUpload>> ExpireOverdueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken) =>
        _inner.ExpireOverdueAsync(now, batchSize, cancellationToken);

    /// <inheritdoc />
    public Task<UploadRecord?> LoadForVerificationAsync(string uploadId, CancellationToken cancellationToken) =>
        _inner.LoadForVerificationAsync(uploadId, cancellationToken);

    /// <inheritdoc />
    public async Task<SnapshotCommitResult> CommitReadyAsync(SnapshotRecord snapshot, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (ReadyResult is { } forced)
        {
            if (Winner is not null)
            {
                await _inner.CommitReadyAsync(Winner, now, cancellationToken);
            }

            return forced;
        }

        return await _inner.CommitReadyAsync(snapshot, now, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> CommitRejectedAsync(string uploadId, UploadRejectionCode code, UploadRejectionReason reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        _inner.CommitRejectedAsync(uploadId, code, reason, now, cancellationToken);
}
