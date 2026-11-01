using System.Globalization;

namespace ReportService.Api.Uploads;

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
