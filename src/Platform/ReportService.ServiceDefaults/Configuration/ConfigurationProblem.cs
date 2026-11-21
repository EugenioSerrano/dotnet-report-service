namespace ReportService.ServiceDefaults;

/// <summary>
/// One configuration problem: the setting it concerns and the reason it is
/// refused. A problem names a setting and never carries the setting's value.
/// </summary>
/// <param name="Setting">The setting path, for example <c>Temporal:Address</c>, or an entry such as <c>consumers[2]</c>.</param>
/// <param name="Reason">Why the setting is refused, in words that never repeat its value.</param>
public sealed record ConfigurationProblem(string Setting, string Reason)
{
    /// <inheritdoc />
    public override string ToString() => $"setting '{Setting}': {Reason}";
}
