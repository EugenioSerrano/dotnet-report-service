using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.Uploads.Tests;

/// <summary>
/// The text forms of the library's values: every enumeration is mapped one way for
/// the stored row and, where a caller sees it, one way for the wire, and every
/// stored text is read back strictly — a text the library does not know is a
/// corrupted row, never a default. A value added without a mapping fails here.
/// </summary>
public sealed class UploadWireValuesTests
{
    [Theory]
    [InlineData(UploadStatus.Created, "created", "CREATED")]
    [InlineData(UploadStatus.Uploading, "uploading", "UPLOADING")]
    [InlineData(UploadStatus.Verifying, "verifying", "VERIFYING")]
    [InlineData(UploadStatus.Ready, "ready", "READY")]
    [InlineData(UploadStatus.Rejected, "rejected", "REJECTED")]
    [InlineData(UploadStatus.Expired, "expired", "EXPIRED")]
    public void EveryStatusHasAWireAndAStoredTextAndReadsBack(UploadStatus status, string wire, string stored)
    {
        Assert.Equal(wire, UploadWireValues.ToWire(status));
        Assert.Equal(stored, UploadWireValues.ToStoredText(status));
        Assert.Equal(status, UploadWireValues.ParseStoredStatus(stored));
    }

    [Theory]
    [InlineData(UploadMode.ApiStream, "API_STREAM", "API_STREAM")]
    [InlineData(UploadMode.DirectStorage, "DIRECT_STORAGE", "DIRECT_STORAGE")]
    public void EveryModeHasAWireAndAStoredTextAndReadsBack(UploadMode mode, string wire, string stored)
    {
        Assert.Equal(wire, UploadWireValues.ToWire(mode));
        Assert.Equal(stored, UploadWireValues.ToStoredText(mode));
        Assert.Equal(mode, UploadWireValues.ParseStoredMode(stored));
    }

    [Fact]
    public void TheProfileHasAWireAndAStoredTextAndReadsBack()
    {
        Assert.Equal("header-items-v1", UploadWireValues.ToWire(DataProfile.HeaderItemsV1));
        Assert.Equal("HEADER_ITEMS_V1", UploadWireValues.ToStoredText(DataProfile.HeaderItemsV1));
        Assert.Equal(DataProfile.HeaderItemsV1, UploadWireValues.ParseStoredProfile("HEADER_ITEMS_V1"));
    }

    [Theory]
    [InlineData(UploadContentEncoding.None, "none")]
    [InlineData(UploadContentEncoding.Gzip, "gzip")]
    [InlineData(UploadContentEncoding.Other, "other")]
    public void EveryEncodingHasAStoredTextAndReadsBack(UploadContentEncoding encoding, string stored)
    {
        Assert.Equal(stored, UploadWireValues.ToStoredText(encoding));
        Assert.Equal(encoding, UploadWireValues.ParseStoredEncoding(stored));
    }

    [Theory]
    [InlineData(UploadContentOutcome.Complete, "complete")]
    [InlineData(UploadContentOutcome.Interrupted, "interrupted")]
    public void EveryOutcomeHasAStoredTextAndReadsBack(UploadContentOutcome outcome, string stored)
    {
        Assert.Equal(stored, UploadWireValues.ToStoredText(outcome));
        Assert.Equal(outcome, UploadWireValues.ParseStoredOutcome(stored));
    }

    [Theory]
    [InlineData(UploadRejectionCode.DataInvalid, "DATA_INVALID")]
    [InlineData(UploadRejectionCode.QuotaExceeded, "QUOTA_EXCEEDED")]
    [InlineData(UploadRejectionCode.UnsupportedCapability, "UNSUPPORTED_CAPABILITY")]
    public void EveryCodeHasAStoredTextAndReadsBack(UploadRejectionCode code, string stored)
    {
        Assert.Equal(stored, UploadWireValues.ToStoredText(code));
        Assert.Equal(code, UploadWireValues.ParseStoredCode(stored));
    }

    [Theory]
    [InlineData(UploadRejectionReason.Sha256Mismatch, "sha256-mismatch")]
    [InlineData(UploadRejectionReason.Truncated, "truncated")]
    [InlineData(UploadRejectionReason.Malformed, "malformed")]
    [InlineData(UploadRejectionReason.Modified, "modified")]
    [InlineData(UploadRejectionReason.Encoding, "encoding")]
    [InlineData(UploadRejectionReason.StoredBytes, "stored-bytes")]
    [InlineData(UploadRejectionReason.InputBytes, "input-bytes")]
    public void EveryReasonHasAStoredTextAndReadsBack(UploadRejectionReason reason, string stored)
    {
        Assert.Equal(stored, UploadWireValues.ToStoredText(reason));
        Assert.Equal(reason, UploadWireValues.ParseStoredReason(stored));
    }

    [Theory]
    [InlineData("created")]
    [InlineData("")]
    [InlineData("READY ")]
    public void AnUnknownStoredStatusIsRefused(string stored)
    {
        Assert.Throws<InvalidOperationException>(() => UploadWireValues.ParseStoredStatus(stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("GZIP")]
    [InlineData("deflate")]
    public void AnUnknownStoredEncodingIsRefused(string stored)
    {
        Assert.Throws<InvalidOperationException>(() => UploadWireValues.ParseStoredEncoding(stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256_mismatch")]
    [InlineData("truncated ")]
    public void AnUnknownStoredReasonIsRefused(string stored)
    {
        Assert.Throws<InvalidOperationException>(() => UploadWireValues.ParseStoredReason(stored));
    }

    [Fact]
    public void ATimestampIsTruncatedToWholeMillisecondsAndFormattedInUtc()
    {
        var value = new DateTimeOffset(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero).AddTicks(4567);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero), UploadWireValues.TruncateToMillisecond(value));
        Assert.Equal("2026-11-01T04:39:23.123Z", UploadWireValues.FormatTimestamp(value));
        Assert.Equal("2026-11-01T04:39:23.123Z", UploadWireValues.FormatTimestamp(value.ToOffset(TimeSpan.FromHours(-3))));
    }
}

/// <summary>The identifier's grammar: what the service creates, and what it agrees to look up.</summary>
public sealed class UploadIdentifierTests
{
    [Fact]
    public void ANewIdentifierHasThePrefixAndThirtyTwoLowerHexCharacters()
    {
        var identifier = UploadIdentifier.Create();

        Assert.Matches("^upload-[0-9a-f]{32}$", identifier);
        Assert.True(UploadIdentifier.IsWellFormed(identifier));
    }

    [Fact]
    public void TwoIdentifiersDiffer()
    {
        Assert.NotEqual(UploadIdentifier.Create(), UploadIdentifier.Create());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("upload-")]
    [InlineData("upload-0123456789ABCDEF0123456789abcdef")]
    [InlineData("upload-0123456789abcdef0123456789abcde")]
    [InlineData("upload-0123456789abcdef0123456789abcdef0")]
    [InlineData("Upload-0123456789abcdef0123456789abcdef")]
    [InlineData("staging-0123456789abcdef0123456789abcdef")]
    [InlineData("upload-0123456789abcdef0123456789abcdeg")]
    public void AnythingElseIsNotAnIdentifier(string? value)
    {
        Assert.False(UploadIdentifier.IsWellFormed(value));
    }
}

/// <summary>
/// The record types the store and the verification share: a snapshot keeps the
/// content's pair apart from the stored object's, and a record is null in every
/// member a step has not set yet.
/// </summary>
public sealed class SnapshotRecordTests
{
    [Fact]
    public void ASnapshotKeepsTheContentPairApartFromTheStoredPair()
    {
        var snapshot = new SnapshotRecord(
            "upload-0123456789abcdef0123456789abcdef",
            "dev-consumer",
            ObjectKey.FromSegments("snapshots", "upload-0123456789abcdef0123456789abcdef", "attempt"),
            UploadContentEncoding.Gzip,
            DataProfile.HeaderItemsV1,
            new string('a', 64),
            322,
            new string('b', 64),
            129,
            new DateTimeOffset(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero));

        Assert.NotEqual(snapshot.Sha256Uncompressed, snapshot.Sha256Stored);
        Assert.NotEqual(snapshot.SizeUncompressed, snapshot.SizeStored);
        Assert.StartsWith("snapshots/", snapshot.ObjectKey.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ANewRecordHasNoTransferOutcomeSnapshotOrRejection()
    {
        var record = new UploadRecord(
            "upload-0123456789abcdef0123456789abcdef",
            "dev-consumer",
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Created,
            ObjectKey.FromSegments("staging", "upload-0123456789abcdef0123456789abcdef", "content"),
            new DateTimeOffset(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 4, 39, 23, 123, TimeSpan.Zero));

        Assert.Null(record.ContentEncoding);
        Assert.Null(record.ContentOutcome);
        Assert.Null(record.ReceivedBytes);
        Assert.Null(record.StagingSize);
        Assert.Null(record.StagingSha256);
        Assert.Null(record.DeclaredSha256);
        Assert.Null(record.CompletedAt);
        Assert.Null(record.RejectionCode);
        Assert.Null(record.RejectionReason);
        Assert.Null(record.Snapshot);
    }

    [Fact]
    public void TheCommitResultNamesEveryOutcome()
    {
        Assert.Equal(3, Enum.GetValues<SnapshotCommitResult>().Length);
        Assert.Contains(SnapshotCommitResult.Committed, Enum.GetValues<SnapshotCommitResult>());
        Assert.Contains(SnapshotCommitResult.AlreadyCommitted, Enum.GetValues<SnapshotCommitResult>());
        Assert.Contains(SnapshotCommitResult.NotVerifying, Enum.GetValues<SnapshotCommitResult>());
    }
}

/// <summary>The upload store contract, run against the in-memory store this library's tests use.</summary>
public sealed class InMemoryUploadStoreContractTests : UploadStoreContract
{
    private readonly InMemoryUploadStore _store = new();

    /// <summary>The store this class's cases run against.</summary>
    protected override Task<IUploadStore> CreateStoreAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IUploadStore>(_store);
}
