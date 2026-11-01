namespace ReportService.Uploads;

/// <summary>How a body's arrival ended: whole, or before its declared end.</summary>
public enum UploadContentOutcome
{
    /// <summary>The whole body arrived.</summary>
    Complete = 0,

    /// <summary>The body ended before its declared end, or the connection failed.</summary>
    Interrupted = 1,
}
