namespace ReportService.ServiceDefaults;

/// <summary>
/// A settings record that checks itself. <see cref="Validate"/> returns one
/// problem per faulty setting, with the setting named relative to the record's
/// configuration section, and never formats a value.
/// </summary>
public interface IValidatedSettings
{
    /// <summary>Returns the problems of this record; empty when it is valid.</summary>
    IEnumerable<ConfigurationProblem> Validate();
}
