using System.Globalization;

namespace ReportService.Uploads;

/// <summary>
/// The text forms of an upload's values. The record store keeps each enumeration
/// in its domain text and parses it back strictly, so a value it cannot read is
/// a corrupted row and never a default; the API answers callers with the wire
/// text, which is lower case for the status and the declared profile. Every
/// mapping is an exhaustive switch that refuses an unmapped value. Timestamps
/// travel as UTC with exactly three fractional digits.
/// </summary>
public static class UploadWireValues
{
    /// <summary>The wire value and the stored text of the streamed mode.</summary>
    public const string ApiStream = "API_STREAM";

    /// <summary>The wire value and the stored text of the direct-to-storage mode.</summary>
    public const string DirectStorage = "DIRECT_STORAGE";

    /// <summary>The wire value of the only admitted data profile.</summary>
    public const string HeaderItemsV1 = "header-items-v1";

    /// <summary>The stored text of the only admitted data profile.</summary>
    public const string HeaderItemsV1Stored = "HEADER_ITEMS_V1";

    /// <summary>The format of a timestamp on the wire and in the record store's responses.</summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>The wire value of <paramref name="status"/>.</summary>
    /// <param name="status">The status to translate.</param>
    public static string ToWire(UploadStatus status) => status switch
    {
        UploadStatus.Created => "created",
        UploadStatus.Uploading => "uploading",
        UploadStatus.Verifying => "verifying",
        UploadStatus.Ready => "ready",
        UploadStatus.Rejected => "rejected",
        UploadStatus.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(status), "the upload status has no wire value"),
    };

    /// <summary>The wire value of <paramref name="mode"/>.</summary>
    /// <param name="mode">The mode to translate.</param>
    public static string ToWire(UploadMode mode) => mode switch
    {
        UploadMode.ApiStream => ApiStream,
        UploadMode.DirectStorage => DirectStorage,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), "the upload mode has no wire value"),
    };

    /// <summary>The wire value of <paramref name="profile"/>.</summary>
    /// <param name="profile">The profile to translate.</param>
    public static string ToWire(DataProfile profile) => profile switch
    {
        DataProfile.HeaderItemsV1 => HeaderItemsV1,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), "the data profile has no wire value"),
    };

    /// <summary>The text the record store keeps for <paramref name="status"/>.</summary>
    /// <param name="status">The status to translate.</param>
    public static string ToStoredText(UploadStatus status) => status switch
    {
        UploadStatus.Created => "CREATED",
        UploadStatus.Uploading => "UPLOADING",
        UploadStatus.Verifying => "VERIFYING",
        UploadStatus.Ready => "READY",
        UploadStatus.Rejected => "REJECTED",
        UploadStatus.Expired => "EXPIRED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), "the upload status has no stored text"),
    };

    /// <summary>The text the record store keeps for <paramref name="mode"/>.</summary>
    /// <param name="mode">The mode to translate.</param>
    public static string ToStoredText(UploadMode mode) => mode switch
    {
        UploadMode.ApiStream => ApiStream,
        UploadMode.DirectStorage => DirectStorage,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), "the upload mode has no stored text"),
    };

    /// <summary>The text the record store keeps for <paramref name="profile"/>.</summary>
    /// <param name="profile">The profile to translate.</param>
    public static string ToStoredText(DataProfile profile) => profile switch
    {
        DataProfile.HeaderItemsV1 => HeaderItemsV1Stored,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), "the data profile has no stored text"),
    };

    /// <summary>Reads the stored text of a status; an unknown text is refused, never defaulted.</summary>
    /// <param name="value">The text the store returned.</param>
    public static UploadStatus ParseStoredStatus(string value) => value switch
    {
        "CREATED" => UploadStatus.Created,
        "UPLOADING" => UploadStatus.Uploading,
        "VERIFYING" => UploadStatus.Verifying,
        "READY" => UploadStatus.Ready,
        "REJECTED" => UploadStatus.Rejected,
        "EXPIRED" => UploadStatus.Expired,
        _ => throw new InvalidOperationException("the stored upload status is not a known value"),
    };

    /// <summary>Reads the stored text of a mode; an unknown text is refused, never defaulted.</summary>
    /// <param name="value">The text the store returned.</param>
    public static UploadMode ParseStoredMode(string value) => value switch
    {
        ApiStream => UploadMode.ApiStream,
        DirectStorage => UploadMode.DirectStorage,
        _ => throw new InvalidOperationException("the stored upload mode is not a known value"),
    };

    /// <summary>Reads the stored text of a data profile; an unknown text is refused, never defaulted.</summary>
    /// <param name="value">The text the store returned.</param>
    public static DataProfile ParseStoredProfile(string value) => value switch
    {
        HeaderItemsV1Stored => DataProfile.HeaderItemsV1,
        _ => throw new InvalidOperationException("the stored data profile is not a known value"),
    };

    /// <summary>The text the record store keeps for <paramref name="encoding"/>.</summary>
    /// <param name="encoding">The encoding to translate.</param>
    public static string ToStoredText(UploadContentEncoding encoding) => encoding switch
    {
        UploadContentEncoding.None => "none",
        UploadContentEncoding.Gzip => "gzip",
        UploadContentEncoding.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), "the content encoding has no stored text"),
    };

    /// <summary>The text the record store keeps for <paramref name="outcome"/>.</summary>
    /// <param name="outcome">The transfer outcome to translate.</param>
    public static string ToStoredText(UploadContentOutcome outcome) => outcome switch
    {
        UploadContentOutcome.Complete => "complete",
        UploadContentOutcome.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), "the transfer outcome has no stored text"),
    };

    /// <summary>The text the record store keeps for <paramref name="code"/>.</summary>
    /// <param name="code">The rejection code to translate.</param>
    public static string ToStoredText(UploadRejectionCode code) => code switch
    {
        UploadRejectionCode.DataInvalid => "DATA_INVALID",
        UploadRejectionCode.QuotaExceeded => "QUOTA_EXCEEDED",
        UploadRejectionCode.UnsupportedCapability => "UNSUPPORTED_CAPABILITY",
        _ => throw new ArgumentOutOfRangeException(nameof(code), "the rejection code has no stored text"),
    };

    /// <summary>The text the record store keeps for <paramref name="reason"/>.</summary>
    /// <param name="reason">The rejection reason to translate.</param>
    public static string ToStoredText(UploadRejectionReason reason) => reason switch
    {
        UploadRejectionReason.Sha256Mismatch => "sha256-mismatch",
        UploadRejectionReason.Truncated => "truncated",
        UploadRejectionReason.Malformed => "malformed",
        UploadRejectionReason.Modified => "modified",
        UploadRejectionReason.Encoding => "encoding",
        UploadRejectionReason.StoredBytes => "stored-bytes",
        UploadRejectionReason.InputBytes => "input-bytes",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), "the rejection reason has no stored text"),
    };

    /// <summary>Reads the stored text of an encoding; an unknown text is refused, never defaulted.</summary>
    /// <param name="value">The text the store returned.</param>
    public static UploadContentEncoding ParseStoredEncoding(string value) => value switch
    {
        "none" => UploadContentEncoding.None,
        "gzip" => UploadContentEncoding.Gzip,
        "other" => UploadContentEncoding.Other,
        _ => throw new InvalidOperationException("the stored content encoding is not a known value"),
    };

    /// <summary>Reads the stored text of a transfer outcome; an unknown text is refused, never defaulted.</summary>
    /// <param name="value">The text the store returned.</param>
    public static UploadContentOutcome ParseStoredOutcome(string value) => value switch
    {
        "complete" => UploadContentOutcome.Complete,
        "interrupted" => UploadContentOutcome.Interrupted,
        _ => throw new InvalidOperationException("the stored transfer outcome is not a known value"),
    };

    /// <summary>Reads the stored text of a rejection code; an unknown text is refused, never defaulted.</summary>
    /// <param name="value">The text the store returned.</param>
    public static UploadRejectionCode ParseStoredCode(string value) => value switch
    {
        "DATA_INVALID" => UploadRejectionCode.DataInvalid,
        "QUOTA_EXCEEDED" => UploadRejectionCode.QuotaExceeded,
        "UNSUPPORTED_CAPABILITY" => UploadRejectionCode.UnsupportedCapability,
        _ => throw new InvalidOperationException("the stored rejection code is not a known value"),
    };

    /// <summary>Reads the stored text of a rejection reason; an unknown text is refused, never defaulted.</summary>
    /// <param name="value">The text the store returned.</param>
    public static UploadRejectionReason ParseStoredReason(string value) => value switch
    {
        "sha256-mismatch" => UploadRejectionReason.Sha256Mismatch,
        "truncated" => UploadRejectionReason.Truncated,
        "malformed" => UploadRejectionReason.Malformed,
        "modified" => UploadRejectionReason.Modified,
        "encoding" => UploadRejectionReason.Encoding,
        "stored-bytes" => UploadRejectionReason.StoredBytes,
        "input-bytes" => UploadRejectionReason.InputBytes,
        _ => throw new InvalidOperationException("the stored rejection reason is not a known value"),
    };

    /// <summary>Truncates <paramref name="value"/> to whole milliseconds in UTC, the precision every response and row carries.</summary>
    /// <param name="value">The value to truncate.</param>
    public static DateTimeOffset TruncateToMillisecond(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    /// <summary>Formats <paramref name="value"/> as the wire timestamp: UTC, three fractional digits, <c>Z</c>.</summary>
    /// <param name="value">The value to format.</param>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
}
