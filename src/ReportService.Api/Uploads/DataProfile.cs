namespace ReportService.Api.Uploads;

/// <summary>
/// The shape the uploaded data declares. Only the first profile is admitted;
/// a request declaring any other shape is refused as an unsupported capability.
/// </summary>
public enum DataProfile
{
    /// <summary>The header-items-v1 profile: a small header followed by an array of records.</summary>
    HeaderItemsV1,
}
