using System.Globalization;

namespace ReportService.Samples.TemplateAuthoring;

/// <summary>
/// Runs the warning rules over a package that rendered and gives the lines to print:
/// ordered (source warnings by file, line and column, then output warnings in document
/// order), each distinct line once, and at most <see cref="MaximumLines"/> of them followed
/// by the count of the rest.
/// </summary>
public sealed class AuthoringWarnings(IReadOnlyList<IWarningRule> rules)
{
    /// <summary>The most warning lines one render prints.</summary>
    public const int MaximumLines = 20;

    /// <summary>The rules the tool runs by default.</summary>
    public static AuthoringWarnings Default { get; } =
        new([new ReservedMemberRule(), new CultureNumberAttributeRule()]);

    /// <summary>The lines to print for <paramref name="input"/>.</summary>
    /// <param name="input">The rendered package.</param>
    public IReadOnlyList<string> Lines(WarningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        List<AuthoringWarning> found = [];
        try
        {
            foreach (var rule in rules)
            {
                found.AddRange(rule.Find(input));
            }
        }
#pragma warning disable CA1031 // A rule's defect must not take the render result with it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return ["internal-error: the warning rules could not run (" + exception.GetType().Name + ")"];
        }

        var lines = found
            .OrderBy(warning => warning.Order)
            .Select(warning => warning.ToLine())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (lines.Count <= MaximumLines)
        {
            return lines;
        }

        var rest = lines.Count - MaximumLines;
        return
        [
            .. lines.Take(MaximumLines),
            "warning: " + rest.ToString(CultureInfo.InvariantCulture) + " more warnings not shown",
        ];
    }
}
