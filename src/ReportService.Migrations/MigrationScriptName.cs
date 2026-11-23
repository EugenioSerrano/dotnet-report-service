using ReportService.ServiceDefaults;

namespace ReportService.Migrations;

/// <summary>
/// The rule a script's file name follows: exactly four digits, an underscore and
/// a lower-case kebab-case slug ending in <c>.sql</c>. The fixed width makes the
/// ordinal order of the names the numeric order of the scripts.
/// </summary>
public static class MigrationScriptName
{
    /// <summary>
    /// The rule itself lives once in <see cref="SchemaVersion"/>, which the
    /// settings check of the expected version also uses.
    /// </summary>
    public static bool IsValid(string? name) => SchemaVersion.IsScriptName(name);

    /// <summary>The four digits a script's name starts with; they order the scripts.</summary>
    public static string Number(string name) => name[..4];

    /// <summary>Why <paramref name="name"/> does not follow the rule, or <c>null</c> when it does.</summary>
    public static string? Problem(string name) =>
        IsValid(name) ? null : $"migration script '{name}' does not follow the name rule 0000_<slug>.sql";
}
