using System.Globalization;

namespace ReportService.Api.Jobs;

/// <summary>
/// The syntax of the template reference this operation accepts today: an
/// identifier made of the characters a storage key segment may hold, and a version
/// of 1 to 128 printable characters without a space. These two rules are a
/// temporary rule of acceptance, not the answer this service will give for a
/// template it cannot find: the step that resolves the reference answers an unknown
/// template, a range and a branch name with its own code, and the change that adds
/// it replaces or keeps this type.
/// </summary>
public static class ProvisionalTemplateReferenceSyntax
{
    /// <summary>The longest template identifier the rule admits.</summary>
    public const int MaxIdentifierLength = 128;

    /// <summary>The longest version the rule admits.</summary>
    public const int MaxVersionLength = 128;

    /// <summary>What an answer says when the identifier breaks the rule.</summary>
    public const string IdentifierDetail = "The field 'template.id' must be a lower-case template identifier.";

    /// <summary>What an answer says when the version breaks the rule.</summary>
    public const string VersionDetail = "The field 'template.version' must be 1 to 128 printable characters without spaces.";

    /// <summary>Whether <paramref name="identifier"/> follows the rule, compared by ordinal.</summary>
    /// <param name="identifier">The value to check.</param>
    public static bool IsIdentifier(string? identifier)
    {
        if (identifier is null || identifier.Length is 0 or > MaxIdentifierLength)
        {
            return false;
        }

        for (var index = 0; index < identifier.Length; index++)
        {
            var character = identifier[index];
            var isEdge = index == 0 || index == identifier.Length - 1;
            var admitted = character is (>= 'a' and <= 'z') or (>= '0' and <= '9')
                || (!isEdge && character is '.' or '_' or '-');
            if (!admitted)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="version"/> follows the rule, compared by ordinal.</summary>
    /// <param name="version">The value to check.</param>
    public static bool IsVersion(string? version)
    {
        if (version is null || version.Length is 0 or > MaxVersionLength)
        {
            return false;
        }

        foreach (var character in version)
        {
            if (character is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The classification of a reference, as the service's log and counter carry it.</summary>
    /// <param name="version">The version the request named, or null.</param>
    public static string FormOf(string? version) =>
        version is null ? "by-name" : "exact";

    /// <summary>The wire text of a reference form, as the log carries it.</summary>
    /// <param name="hasVersion">Whether the request named a version.</param>
    public static string FormOf(bool hasVersion) =>
        (hasVersion ? "exact" : "by-name").ToString(CultureInfo.InvariantCulture);
}
