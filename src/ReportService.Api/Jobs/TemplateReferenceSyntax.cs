using ReportService.TemplatePackages.Versioning;

namespace ReportService.Api.Jobs;

/// <summary>
/// The final syntax of a template reference at acceptance, and the one answer a
/// reference this service can never use receives. An identifier is a lower-case
/// template identifier, the characters a storage key segment and a source folder
/// may hold; a version, when the request names one, is a semantic version by its
/// syntax alone. A range, a branch name and a partial version are therefore not
/// references: nothing can be taken for them, so they are answered as a template
/// that cannot be found, before the database or the source is touched, and with
/// the same problem a version this consumer may not use receives.
/// </summary>
public static class TemplateReferenceSyntax
{
    /// <summary>The longest template identifier the grammar admits.</summary>
    public const int MaxIdentifierLength = 128;

    /// <summary>The title of the one answer of a reference that cannot be used.</summary>
    public const string NotFoundTitle = "Template not found";

    /// <summary>The detail of that answer. It names no sent value, ever.</summary>
    public const string NotFoundDetail = "The template or template version was not found.";

    /// <summary>The log reason of a reference that broke the syntax.</summary>
    public const string SyntaxReason = "reference-syntax";

    /// <summary>The log reason of a version this consumer may not use.</summary>
    public const string NotUsableReason = "not-usable";

    /// <summary>How a reference names its template version.</summary>
    public enum ReferenceKind
    {
        /// <summary>The request named a version, and it is a semantic version.</summary>
        Exact,

        /// <summary>The request named only the template.</summary>
        ByName,

        /// <summary>The reference cannot name anything this service could take.</summary>
        NotFound,
    }

    /// <summary>
    /// Classifies a reference. A template identifier must be a lower-case identifier of 1
    /// to 128 characters beginning and ending with a letter or a digit; a version, when
    /// present, must be a semantic version of at most 128 characters. Anything else is
    /// <see cref="ReferenceKind.NotFound"/>.
    /// </summary>
    /// <param name="templateId">The template's identifier, as the request wrote it.</param>
    /// <param name="version">The version the request named, or null for a reference by name.</param>
    public static ReferenceKind Check(string? templateId, string? version)
    {
        if (!IsIdentifier(templateId))
        {
            return ReferenceKind.NotFound;
        }

        if (version is null)
        {
            return ReferenceKind.ByName;
        }

        return SemanticVersionSyntax.IsValid(version) ? ReferenceKind.Exact : ReferenceKind.NotFound;
    }

    /// <summary>Whether <paramref name="templateId"/> is a template identifier, compared by ordinal.</summary>
    /// <param name="templateId">The value to check.</param>
    public static bool IsIdentifier(string? templateId)
    {
        if (templateId is null || templateId.Length is 0 or > MaxIdentifierLength)
        {
            return false;
        }

        for (var index = 0; index < templateId.Length; index++)
        {
            var character = templateId[index];
            var isEdge = index == 0 || index == templateId.Length - 1;
            var admitted = character is (>= 'a' and <= 'z') or (>= '0' and <= '9')
                || (!isEdge && character is '.' or '_' or '-');
            if (!admitted)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The classification of a reference as the service's log and counter carry it.</summary>
    /// <param name="version">The version the request named, or null.</param>
    public static string FormOf(string? version) => version is null ? "by-name" : "exact";
}
