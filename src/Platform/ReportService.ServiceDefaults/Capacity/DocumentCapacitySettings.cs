namespace ReportService.ServiceDefaults;

/// <summary>
/// The deployment's single-document capacity, as configured: the most pages and the
/// most bytes of committed parts one PDF output may reach, and the name of the profile
/// those values came from. Every setting is optional: a deployment that sets none runs
/// the example profile the product ships. A deployment that sets a value must also name
/// its profile, so a measured value is never reported under the example's name. A
/// refused value stops the process at start, naming the setting and never its value.
/// </summary>
public sealed class DocumentCapacitySettings : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "DocumentCapacity";

    /// <summary>The setting of the page capacity, relative to <see cref="SectionPath"/>.</summary>
    public const string MaximumPagesSetting = "MaximumPages";

    /// <summary>The setting of the parts-bytes capacity, relative to <see cref="SectionPath"/>.</summary>
    public const string MaximumPartBytesSetting = "MaximumPartBytes";

    /// <summary>The setting of the profile name, relative to <see cref="SectionPath"/>.</summary>
    public const string ProfileNameSetting = "ProfileName";

    /// <summary>The longest accepted profile name.</summary>
    public const int MaximumProfileNameLength = 64;

    /// <summary>The most pages one output may reach, when configured.</summary>
    public int? MaximumPages { get; set; }

    /// <summary>The most bytes of committed parts one output may reach, when configured.</summary>
    public long? MaximumPartBytes { get; set; }

    /// <summary>The deployment's name for this profile, when configured.</summary>
    public string? ProfileName { get; set; }

    /// <summary>Whether the deployment configured any capacity value.</summary>
    public bool IsConfigured => MaximumPages is not null || MaximumPartBytes is not null;

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (MaximumPages is <= 0)
        {
            yield return new ConfigurationProblem(MaximumPagesSetting, "must be greater than zero");
        }

        if (MaximumPartBytes is <= 0)
        {
            yield return new ConfigurationProblem(MaximumPartBytesSetting, "must be greater than zero");
        }

        if (ProfileName is not null && !IsValidProfileName(ProfileName))
        {
            yield return new ConfigurationProblem(
                ProfileNameSetting,
                "must be 1 to 64 printable ASCII characters");
        }
        else if (IsConfigured && ProfileName is null)
        {
            yield return new ConfigurationProblem(
                ProfileNameSetting,
                "is required when a capacity value is configured");
        }
    }

    private static bool IsValidProfileName(string name) =>
        name.Length is > 0 and <= MaximumProfileNameLength
        && name.All(character => character is >= '!' and <= '~');
}
