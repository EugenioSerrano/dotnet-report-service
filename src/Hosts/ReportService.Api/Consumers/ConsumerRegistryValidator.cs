using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using ReportService.ServiceDefaults;

namespace ReportService.Api.Consumers;

/// <summary>The outcome of validating a registry: a snapshot when every entry is valid, otherwise the problems; warnings either way.</summary>
/// <param name="Snapshot">The validated registry, or <c>null</c> when any entry is faulty.</param>
/// <param name="Problems">One problem per faulty entry, naming the entry and never a value.</param>
/// <param name="Warnings">Ignored unknown properties, each named with its entry.</param>
public sealed record ConsumerRegistryValidation(
    ConsumerRegistrySnapshot? Snapshot,
    IReadOnlyList<ConfigurationProblem> Problems,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Validates the <c>consumers</c> list of the configuration. An identity is
/// required, unique (ordinal comparison), at most 128 characters of
/// <c>A-Z a-z 0-9 . _ -</c>; <c>active</c> is required and boolean;
/// <c>displayName</c> is at most 200 characters; unknown properties are
/// ignored with a warning. Problems name the entry by index, and by identity
/// only when the identity itself is valid; no value is ever repeated.
/// </summary>
public static partial class ConsumerRegistryValidator
{
    /// <summary>The configuration key of the list.</summary>
    public const string ListKey = "consumers";

    private const int MaximumIdentityLength = 128;
    private const int MaximumDisplayNameLength = 200;

    private static readonly HashSet<string> KnownProperties =
        new(["consumerId", "displayName", "active", "authenticationSubject", "createdAt"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Validates the registry held by <paramref name="configuration"/>.</summary>
    public static ConsumerRegistryValidation Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var list = configuration.GetChildren().FirstOrDefault(c => string.Equals(c.Key, ListKey, StringComparison.OrdinalIgnoreCase));
        if (list is null)
        {
            return Refused(new ConfigurationProblem(ListKey, "the registry has no consumers list (the file is missing, empty or not valid JSON)"));
        }

        var items = list.GetChildren().ToList();
        if ((items.Count == 0 && !string.IsNullOrEmpty(list.Value)) || items.Any(i => !IsIndex(i.Key)))
        {
            return Refused(new ConfigurationProblem(ListKey, "consumers must be a list of entries"));
        }

        var problems = new List<ConfigurationProblem>();
        var warnings = new List<string>();
        var entries = new List<ConsumerEntry>();
        var firstIndexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items.OrderBy(i => int.Parse(i.Key, CultureInfo.InvariantCulture)))
        {
            var index = int.Parse(item.Key, CultureInfo.InvariantCulture);
            var (entry, problem, label) = ValidateEntry(item, index, firstIndexOf);
            warnings.AddRange(item.GetChildren()
                .Where(p => !KnownProperties.Contains(p.Key))
                .Select(p => $"{label}: unknown property '{p.Key}' is ignored"));
            if (problem is not null)
            {
                problems.Add(problem);
            }
            else
            {
                firstIndexOf[entry!.ConsumerId] = index;
                entries.Add(entry);
            }
        }

        return problems.Count > 0
            ? new ConsumerRegistryValidation(null, problems, warnings)
            : new ConsumerRegistryValidation(ConsumerRegistrySnapshot.Create(entries), [], warnings);
    }

    private static (ConsumerEntry? Entry, ConfigurationProblem? Problem, string Label) ValidateEntry(
        IConfigurationSection item, int index, Dictionary<string, int> firstIndexOf)
    {
        var setting = $"{ListKey}[{index}]";
        var identity = item["consumerId"];
        if (string.IsNullOrWhiteSpace(identity))
        {
            return (null, new ConfigurationProblem(setting, "consumerId is empty"), setting);
        }

        if (identity.Length > MaximumIdentityLength)
        {
            return (null, new ConfigurationProblem(setting, $"consumerId is longer than {MaximumIdentityLength} characters"), setting);
        }

        if (!IdentityCharacters().IsMatch(identity))
        {
            return (null, new ConfigurationProblem(setting, "consumerId has a character outside A-Z, a-z, 0-9, '.', '_' and '-'"), setting);
        }

        var label = $"{setting} (consumer '{identity}')";
        if (firstIndexOf.TryGetValue(identity, out var first))
        {
            return (null, new ConfigurationProblem(setting, $"consumer '{identity}' duplicates {ListKey}[{first}]"), label);
        }

        var active = item["active"];
        if (active is null)
        {
            return (null, new ConfigurationProblem(setting, $"consumer '{identity}': active is required"), label);
        }

        if (!bool.TryParse(active, out var isActive))
        {
            return (null, new ConfigurationProblem(setting, $"consumer '{identity}': active must be true or false"), label);
        }

        var displayName = item["displayName"];
        if (displayName is { Length: > MaximumDisplayNameLength })
        {
            return (null, new ConfigurationProblem(setting, $"consumer '{identity}': displayName is longer than {MaximumDisplayNameLength} characters"), label);
        }

        return (new ConsumerEntry(identity, displayName, isActive, item["authenticationSubject"], item["createdAt"]), null, label);
    }

    private static ConsumerRegistryValidation Refused(ConfigurationProblem problem) => new(null, [problem], []);

    private static bool IsIndex(string key) =>
        int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityCharacters();
}
