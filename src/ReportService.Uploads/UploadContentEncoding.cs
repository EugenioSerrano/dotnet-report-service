namespace ReportService.Uploads;

/// <summary>
/// How the bytes a consumer sends are encoded. <see cref="None"/> and
/// <see cref="Gzip"/> are the admitted encodings; <see cref="Other"/> records a
/// declaration the service does not decode, which is refused when the upload is
/// verified.
/// </summary>
public enum UploadContentEncoding
{
    /// <summary>No content encoding: the bytes are the content.</summary>
    None = 0,

    /// <summary>gzip, as one complete member.</summary>
    Gzip = 1,

    /// <summary>A declared encoding the service does not decode.</summary>
    Other = 2,
}
