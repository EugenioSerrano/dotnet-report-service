using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ReportService.Eng.TestRunner;

/// <summary>The coverage floor of one product assembly that has a unit/component test project.</summary>
internal sealed record CoverageFloor(string Assembly, int LinePercent, int BranchPercent, string Tier);

/// <summary>What one Cobertura report says about one assembly, counted from its lines.</summary>
internal sealed record AssemblyFigures(
    string Assembly,
    string Project,
    string Report,
    int Lines,
    int LinesCovered,
    int BranchPoints,
    int BranchesCovered,
    string? LineRateAttribute,
    string? BranchRateAttribute);

/// <summary>The floor result of one assembly.</summary>
internal sealed record CoverageResult(
    string Assembly,
    string Result,
    string Reason,
    int? LinePercentFloor,
    int? BranchPercentFloor,
    int Lines,
    int LinesCovered,
    int BranchPoints,
    int BranchesCovered,
    string LineRate,
    string BranchRate,
    string? LineRateAttribute,
    string? BranchRateAttribute,
    string? Report)
{
    public bool Failed => Result == CoverageFloors.Fail;
}

/// <summary>
/// The coverage floors of the hermetic level and their evaluation from the
/// Cobertura reports of a run. The table decides which assemblies must be
/// measured: a unit/component test project adds its product assembly here in
/// the same change, so an assembly cannot drop out of the check unnoticed.
/// </summary>
internal static partial class CoverageFloors
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string NotApplicable = "n/a";
    public const string NoFloor = "no floor";

    /// <summary>One row per product assembly with a unit/component test project.</summary>
    public static readonly IReadOnlyList<CoverageFloor> Floors =
    [
        new("ReportService.Api", LinePercent: 80, BranchPercent: 70, Tier: "standard"),
    ];

    /// <summary>
    /// Coverage options of every hermetic project. Given on the command line,
    /// they are merged with the coverage tool's default exclusions (a
    /// testconfig.json would replace those defaults instead).
    /// </summary>
    public static readonly IReadOnlyList<string> Options =
    [
        "--coverlet",
        "--coverlet-output-format", "cobertura",
        "--coverlet-include", "[ReportService.*]*",
        "--coverlet-exclude", "[ReportService.AppHost]*",
        "--coverlet-exclude", "[ReportService.Eng.*]*",
        "--coverlet-exclude", "[ReportService.Samples.*]*",
        "--coverlet-exclude", "[ReportService.Testing]*",
        "--coverlet-exclude-by-file", "**/Program.cs",
        "--coverlet-exclude-by-file", "**/obj/**",
    ];

    /// <summary>The file name pattern of a Cobertura report written by the coverage tool.</summary>
    public const string ReportPattern = "*coverage.cobertura*.xml";

    /// <summary>Reads every <c>package</c> of a Cobertura report, counting its coverable lines and branch points; <paramref name="reportName"/> is how the report is named in the evidence.</summary>
    public static IReadOnlyList<AssemblyFigures> Read(string reportPath, string reportName, string project)
    {
        var document = XDocument.Load(reportPath);
        var figures = new List<AssemblyFigures>();
        foreach (var package in document.Descendants("package"))
        {
            var name = (string?)package.Attribute("name") ?? string.Empty;
            int lines = 0, linesCovered = 0, branchPoints = 0, branchesCovered = 0;

            // A line appears under its class and again under its method: count the class lines only.
            foreach (var line in package.Elements("classes").Elements("class").Elements("lines").Elements("line"))
            {
                lines++;
                if (long.Parse((string?)line.Attribute("hits") ?? "0", CultureInfo.InvariantCulture) > 0)
                {
                    linesCovered++;
                }

                if (string.Equals((string?)line.Attribute("branch"), "true", StringComparison.OrdinalIgnoreCase)
                    && ConditionCoverage().Match((string?)line.Attribute("condition-coverage") ?? string.Empty) is { Success: true } match)
                {
                    branchesCovered += int.Parse(match.Groups["covered"].Value, CultureInfo.InvariantCulture);
                    branchPoints += int.Parse(match.Groups["total"].Value, CultureInfo.InvariantCulture);
                }
            }

            figures.Add(new AssemblyFigures(
                name, project, reportName, lines, linesCovered, branchPoints, branchesCovered,
                (string?)package.Attribute("line-rate"), (string?)package.Attribute("branch-rate")));
        }

        return figures;
    }

    /// <summary>
    /// Evaluates the floors. Figures are compared as exact integer ratios
    /// (covered × 100 ≥ floor × total), so a rate is never rounded up to meet
    /// a floor; the rates the report states are kept beside them.
    /// </summary>
    public static IReadOnlyList<CoverageResult> Evaluate(IReadOnlyList<AssemblyFigures> figures, IReadOnlyList<CoverageFloor> floors)
    {
        var results = new List<CoverageResult>();
        foreach (var floor in floors)
        {
            var found = figures.Where(f => f.Assembly == floor.Assembly).ToList();
            if (found.Count == 0)
            {
                results.Add(Result(floor.Assembly, Fail, "absent from the coverage report", floor, null));
                continue;
            }

            if (found.Select(f => f.Project).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                var projects = string.Join(", ", found.Select(f => f.Project).Distinct(StringComparer.Ordinal));
                results.Add(Result(floor.Assembly, Fail, $"reported by several projects ({projects}); their combination is not defined", floor, found[0]));
                continue;
            }

            var figure = found[0];
            if (figure.Lines == 0)
            {
                results.Add(Result(floor.Assembly, NotApplicable, "no coverable line after the listed exclusions", floor, figure));
                continue;
            }

            var reasons = new List<string>();
            if (figure.LinesCovered * 100L < floor.LinePercent * (long)figure.Lines)
            {
                reasons.Add($"line {Percent(figure.LinesCovered, figure.Lines)} < {floor.LinePercent}%");
            }

            var branchNote = string.Empty;
            if (figure.BranchPoints == 0)
            {
                branchNote = "branch n/a: no branch point";
            }
            else if (figure.BranchesCovered * 100L < floor.BranchPercent * (long)figure.BranchPoints)
            {
                reasons.Add($"branch {Percent(figure.BranchesCovered, figure.BranchPoints)} < {floor.BranchPercent}%");
            }

            results.Add(reasons.Count > 0
                ? Result(floor.Assembly, Fail, string.Join("; ", reasons), floor, figure)
                : Result(floor.Assembly, Pass, branchNote.Length > 0 ? branchNote : "floors met", floor, figure));
        }

        foreach (var figure in figures.Where(f => floors.All(floor => floor.Assembly != f.Assembly)))
        {
            results.Add(Result(figure.Assembly, NoFloor, "reported, no floor (no test project)", null, figure));
        }

        return results;
    }

    /// <summary>A percentage truncated (never rounded up) to two decimals.</summary>
    public static string Percent(int covered, int total) => total == 0
        ? "—"
        : (Math.Floor(covered * 10000m / total) / 100m).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    private static CoverageResult Result(string assembly, string result, string reason, CoverageFloor? floor, AssemblyFigures? figure) =>
        new(
            assembly,
            result,
            reason,
            floor?.LinePercent,
            floor?.BranchPercent,
            figure?.Lines ?? 0,
            figure?.LinesCovered ?? 0,
            figure?.BranchPoints ?? 0,
            figure?.BranchesCovered ?? 0,
            figure is null ? "—" : Percent(figure.LinesCovered, figure.Lines),
            figure is null ? "—" : Percent(figure.BranchesCovered, figure.BranchPoints),
            figure?.LineRateAttribute,
            figure?.BranchRateAttribute,
            figure?.Report);

    [GeneratedRegex(@"\((?<covered>\d+)/(?<total>\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ConditionCoverage();
}
