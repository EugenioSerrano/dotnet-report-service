using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ReportService.ServiceDefaults;

/// <summary>How the schema a process finds compares with the version it expects.</summary>
public enum SchemaStatus
{
    /// <summary>The applied version is the expected one.</summary>
    Current,

    /// <summary>The applied version is below the expected one.</summary>
    Behind,

    /// <summary>The applied version is above the expected one.</summary>
    Ahead,

    /// <summary>A journal entry is not a script the code embeds.</summary>
    Unknown,
}

/// <summary>
/// The comparison of an expected schema version with the versions a database
/// reports: the status, the two versions and, when the code knows its own
/// scripts, the first entry it does not know.
/// </summary>
public sealed record SchemaAssessment(SchemaStatus Status, string Expected, string Applied, string? Offender = null);

/// <summary>
/// The order and the names of the numbered migration scripts, and the
/// comparison of an expected version with the applied ones. One rule, shared by
/// the migration runner and the database health check.
/// </summary>
public static partial class SchemaVersion
{
    /// <summary>The version of an empty script set.</summary>
    public const string None = "none";

    /// <summary>
    /// Whether <paramref name="name"/> is a migration script name: four digits,
    /// an underscore, a lower-case kebab-case slug and <c>.sql</c>. The fixed
    /// width makes the ordinal order of the names the numeric order of the
    /// scripts.
    /// </summary>
    public static bool IsScriptName([NotNullWhen(true)] string? name) =>
        name is not null && ScriptNamePattern().IsMatch(name);

    /// <summary>
    /// Assesses the applied names against <paramref name="expected"/>. With
    /// <paramref name="known"/>, an applied name outside it is
    /// <see cref="SchemaStatus.Unknown"/>. Without it only the highest applied
    /// name is compared, so a name below the expected version is not detected
    /// here — the migration runner, which embeds every script, refuses it
    /// before a process starts.
    /// </summary>
    public static SchemaAssessment Assess(
        string expected,
        IEnumerable<string?> applied,
        IReadOnlyCollection<string>? known = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);
        ArgumentNullException.ThrowIfNull(applied);

        var names = applied
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToList();
        var highest = names.Count == 0 ? None : names[^1];

        if (known is not null && names.FirstOrDefault(name => !known.Contains(name)) is { } offender)
        {
            return new SchemaAssessment(SchemaStatus.Unknown, expected, highest, offender);
        }

        if (Compare(highest, expected) > 0)
        {
            return new SchemaAssessment(SchemaStatus.Ahead, expected, highest);
        }

        var everyKnownApplied = known is null || known.All(names.Contains);
        return Compare(highest, expected) == 0 && everyKnownApplied
            ? new SchemaAssessment(SchemaStatus.Current, expected, highest)
            : new SchemaAssessment(SchemaStatus.Behind, expected, highest);
    }

    /// <summary>Orders two versions, with <see cref="None"/> below every script name.</summary>
    private static int Compare(string version, string other) => (version, other) switch
    {
        (None, None) => 0,
        (None, _) => -1,
        (_, None) => 1,
        _ => string.CompareOrdinal(version, other),
    };

    [GeneratedRegex(@"^[0-9]{4}_[a-z0-9]+(-[a-z0-9]+)*\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptNamePattern();
}
