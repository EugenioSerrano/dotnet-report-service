namespace ReportService.Uploads;

/// <summary>
/// The way a consumer's data reaches the service: streamed through the API, or
/// written straight to storage through a destination the storage provider
/// signs. The text the database stores for each value and the text the API
/// answers with live in <see cref="UploadWireValues"/>.
/// </summary>
public enum UploadMode
{
    /// <summary>The bytes are streamed through the API.</summary>
    ApiStream,

    /// <summary>The bytes go straight to storage through a signed destination.</summary>
    DirectStorage,
}
