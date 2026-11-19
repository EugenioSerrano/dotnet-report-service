namespace ReportService.Templates;

/// <summary>
/// A block whose presentation the service cannot fix: the job's culture, time zone or
/// clock is absent, or the culture or the zone is a name the runtime does not know. The
/// block fails closed with nothing composed; the refusal names the job member and never a
/// value of the data.
/// </summary>
public sealed class PresentationUnavailable : Exception
{
    /// <summary>Creates the refusal for <paramref name="field"/>.</summary>
    /// <param name="field">The job member that is absent or unusable.</param>
    public PresentationUnavailable(string field)
        : base($"The job has no usable {field}.")
    {
        Field = field;
    }

    /// <summary>The job member that is absent or unusable.</summary>
    public string Field { get; }

    /// <summary>The member names a job is read for.</summary>
    public static class Fields
    {
        /// <summary>The job's culture.</summary>
        public const string Culture = "culture";

        /// <summary>The job's time zone.</summary>
        public const string TimeZone = "time-zone";

        /// <summary>The job's fixed clock.</summary>
        public const string Clock = "clock";
    }
}
