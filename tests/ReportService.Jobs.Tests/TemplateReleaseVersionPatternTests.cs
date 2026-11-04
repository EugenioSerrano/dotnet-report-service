using System.Text.RegularExpressions;
using ReportService.Testing;
using ReportService.TemplatePackages.Versioning;

namespace ReportService.Jobs.Tests;

/// <summary>
/// The database's guard of a release version agrees with the code's parser: the pattern
/// the template-resolution script puts on the baseline and on a by-name pin accepts exactly
/// the versions the service calls a release - a semantic version with no pre-release part -
/// and refuses every pre-release, every range, every branch name and every partial version.
/// The pattern is a guard, never the parser: it is compared here so the two can never drift.
/// </summary>
public sealed class TemplateReleaseVersionPatternTests
{
    /// <summary>The script that carries the guard.</summary>
    public const string Script = "0009_template-resolution.sql";

    private static readonly string LongestVersion = "1.0.0+" + new string('a', 122);

    public static TheoryData<string> Versions { get; } =
    [
        "0.0.0",
        "1.0.0",
        "1.1.0",
        "1.10.0",
        "1.0.0+build.5",
        "1.0.0+build.5.6",
        LongestVersion,
        "2.0.0-beta.1",
        "1.0.0-rc.1+b",
        "",
        "1.0",
        "1",
        "v1.0.0",
        "01.0.0",
        "1.0.0-",
        "^1.0.0",
        "~1.2",
        ">=1.0.0",
        "1.x",
        "1.0.*",
        "1.0 - 2.0",
        "main",
        "develop",
        "feature/x",
        "latest",
    ];

    [Fact]
    public void TheScriptGuardsTheBaselineAndTheByNamePinWithOnePattern()
    {
        var sql = ScriptSql();

        Assert.Contains("template_references_baseline", sql, StringComparison.Ordinal);
        Assert.Contains("template_references_by_name_release", sql, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(sql, @"'(?:\^\(0\|\[1-9\]).*?\$'").Count);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void TheDatabaseGuardAcceptsExactlyTheVersionsTheServiceCallsARelease(string version)
    {
        var guard = Guard();

        if (IsRelease(version))
        {
            Assert.Matches(guard, version);
        }
        else
        {
            Assert.DoesNotMatch(guard, version);
        }
    }

    [Fact]
    public void TheGuardCarriesNoLengthBoundAndTheParserDoes()
    {
        // The guard answers "is this a release version", and the 128-character bound is the
        // parser's: the API's syntax check applies it before any value is stored, so the
        // database never has to.
        var longer = "1.0.0+" + new string('a', 200);

        Assert.Matches(Guard(), longer);
        Assert.False(SemanticVersionSyntax.IsValid(longer));
    }

    // The pattern as the script writes it: one pattern, found twice, and identical both times.
    private static Regex Guard()
    {
        var matches = Regex.Matches(ScriptSql(), @"'(\^\(0\|\[1-9\]\[0-9\]\*\)[^']*)'")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Single(matches);
        return new Regex(matches[0], RegexOptions.CultureInvariant);
    }

    private static string ScriptSql() => File.ReadAllText(
        Path.Combine(RepositoryPaths.Root, "src", "ReportService.Migrations", "Migrations", Script));

    private static bool IsRelease(string version) =>
        SemanticVersion.TryParse(version, out var parsed) && parsed.PreRelease.Count == 0;
}
