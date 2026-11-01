using System.Globalization;
using System.Text.RegularExpressions;

namespace ReportService.ServiceDefaults.Temporal;

/// <summary>
/// Where the workflow engine is: <c>Temporal:Address</c> as <c>host:port</c>
/// (no scheme, no credential) and <c>Temporal:Namespace</c>. Neither is a
/// secret; both are required.
/// </summary>
public sealed partial class WorkflowEngineOptions : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "Temporal";

    /// <summary>The engine's gRPC endpoint, <c>host:port</c>.</summary>
    public string? Address { get; set; }

    /// <summary>The engine namespace the process works in.</summary>
    public string Namespace { get; set; } = "default";

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (string.IsNullOrWhiteSpace(Address))
        {
            yield return new ConfigurationProblem(nameof(Address), "is required");
        }
        else if (HostAndPort().Match(Address) is not { Success: true } match
            || !int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            yield return new ConfigurationProblem(nameof(Address), "must be host:port");
        }

        if (string.IsNullOrWhiteSpace(Namespace))
        {
            yield return new ConfigurationProblem(nameof(Namespace), "is required");
        }
    }

    [GeneratedRegex(@"^(?:[A-Za-z0-9](?:[A-Za-z0-9.\-]*[A-Za-z0-9])?|\[[0-9A-Fa-f:.]+\]):(?<port>[0-9]{1,5})$", RegexOptions.CultureInvariant)]
    private static partial Regex HostAndPort();
}
