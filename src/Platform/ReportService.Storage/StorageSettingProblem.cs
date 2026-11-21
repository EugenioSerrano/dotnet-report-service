namespace ReportService.Storage;

/// <summary>
/// One faulty storage setting: the setting, relative to the provider's
/// configuration section, and why it is refused — never its value.
/// </summary>
/// <param name="Setting">The setting, for example <c>Root</c>.</param>
/// <param name="Reason">Why the setting is refused, in words that never repeat its value.</param>
public sealed record StorageSettingProblem(string Setting, string Reason);
