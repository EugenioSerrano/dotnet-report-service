using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ReportService.Eng.TestRunner;

/// <summary>
/// The coverage floor of one product assembly that has a unit/component test
/// project. The floor names both the assembly and the test project that
/// measures it: one row is one pair, so an assembly reported by a second
/// project never has two competing figures.
/// </summary>
internal sealed record CoverageFloor(string Assembly, string Project, int LinePercent, int BranchPercent, string Tier);

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

/// <summary>The floor result of one assembly, or the record of a figure the floors do not take.</summary>
internal sealed record CoverageResult(
    string Assembly,
    string Project,
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
/// measured and by which project: a unit/component test project adds its
/// product assembly here in the same change, so an assembly cannot drop out of
/// the check unnoticed. A floored assembly is measured from its own test
/// project's report only; the same assembly found in another project's report
/// is recorded and excluded, never combined with the owning figure and never
/// taken as the better one.
/// </summary>
internal static partial class CoverageFloors
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string NotApplicable = "n/a";
    public const string Excluded = "excluded";
    public const string NoFloor = "no floor";

    /// <summary>One row per product assembly with a unit/component test project, naming that project.</summary>
    public static readonly IReadOnlyList<CoverageFloor> Floors =
    [
        new("ReportService.Api", Project: "ReportService.Api.Tests", LinePercent: 80, BranchPercent: 70, Tier: "standard"),
        new("ReportService.Data", Project: "ReportService.Data.Tests", LinePercent: 90, BranchPercent: 85, Tier: "integrity"),
        new("ReportService.Jobs", Project: "ReportService.Jobs.Tests", LinePercent: 80, BranchPercent: 70, Tier: "standard"),
        new("ReportService.Migrations", Project: "ReportService.Migrations.Tests", LinePercent: 80, BranchPercent: 70, Tier: "standard"),
        new("ReportService.Storage", Project: "ReportService.Storage.Tests", LinePercent: 80, BranchPercent: 70, Tier: "standard"),
        new("ReportService.Storage.Filesystem", Project: "ReportService.Storage.Tests", LinePercent: 80, BranchPercent: 70, Tier: "standard"),
        new("ReportService.Uploads", Project: "ReportService.Uploads.Tests", LinePercent: 80, BranchPercent: 70, Tier: "standard"),
        new("ReportService.Rendering", Project: "ReportService.Rendering.Tests", LinePercent: 80, BranchPercent: 70, Tier: "standard"),
        new("ReportService.Templates", Project: "ReportService.Templates.Tests", LinePercent: 80, BranchPercent: 70, Tier: "standard"),
        new("ReportService.TemplatePackages", Project: "ReportService.TemplatePackages.Tests", LinePercent: 80, BranchPercent: 70, Tier: "standard"),
        new("ReportService.TemplateSource", Project: "ReportService.TemplateSource.Tests", LinePercent: 80, BranchPercent: 70, Tier: "standard"),
        new("ReportService.Workers.Orchestration", Project: "ReportService.Workers.Orchestration.Tests", LinePercent: 80, BranchPercent: 70, Tier: "standard"),
        new("ReportService.Workers.Pdf", Project: "ReportService.Workers.Pdf.Tests", LinePercent: 80, BranchPercent: 70, Tier: "standard"),
    ];

    /// <summary>
    /// Which product assemblies each hermetic test project owns. One assembly is
    /// measured by one project only: the run passes each project the include of the
    /// assemblies it owns, so an assembly can neither be measured twice nor be
    /// measured by a project that merely exercises it. A project without an entry
    /// here cannot run at all.
    /// </summary>
    public static readonly IReadOnlyList<(string Project, IReadOnlyList<string> Assemblies)> Owners =
    [
        ("ReportService.Api.Tests", ["ReportService.Api"]),
        ("ReportService.Data.Tests", ["ReportService.Data"]),
        ("ReportService.Jobs.Tests", ["ReportService.Jobs"]),
        ("ReportService.Migrations.Tests", ["ReportService.Migrations"]),
        ("ReportService.Storage.Tests", ["ReportService.Storage", "ReportService.Storage.Filesystem"]),
        ("ReportService.Uploads.Tests", ["ReportService.Uploads"]),
        ("ReportService.Rendering.Tests", ["ReportService.Rendering"]),
        ("ReportService.Templates.Tests", ["ReportService.Templates"]),
        ("ReportService.TemplatePackages.Tests", ["ReportService.TemplatePackages"]),
        ("ReportService.TemplateSource.Tests", ["ReportService.TemplateSource"]),
        // The fixture library and the test template packages are test support:
        // they hold no product behaviour, so the project measures them and the
        // run reports them without a floor.
        ("ReportService.Fixtures.Tests", ["ReportService.Testing"]),
        ("ReportService.Workers.Orchestration.Tests", ["ReportService.Workers.Orchestration"]),
        ("ReportService.Workers.Pdf.Tests", ["ReportService.Workers.Pdf"]),
    ];

    /// <summary>The assemblies <paramref name="project"/> measures, or <see langword="null"/> when it owns none.</summary>
    public static IReadOnlyList<string>? OwnedAssemblies(string project) =>
        Owners.FirstOrDefault(owner => string.Equals(owner.Project, project, StringComparison.Ordinal)).Assemblies;

    /// <summary>The coverage include arguments of the assemblies <paramref name="project"/> owns.</summary>
    public static IReadOnlyList<string> ProjectIncludes(string project)
    {
        var assemblies = OwnedAssemblies(project);
        if (assemblies is null)
        {
            return [];
        }

        var arguments = new List<string>();
        foreach (var assembly in assemblies)
        {
            arguments.Add("--coverlet-include");
            arguments.Add($"[{assembly}]*");
        }

        return arguments;
    }

    /// <summary>
    /// Coverage options of every hermetic project. Given on the command line,
    /// they are merged with the coverage tool's default exclusions (a
    /// testconfig.json would replace those defaults instead). The include is not
    /// given here: each project is measured on the assemblies it owns.
    /// </summary>
    public static readonly IReadOnlyList<string> Options =
    [
        "--coverlet",
        "--coverlet-output-format", "cobertura",
        "--coverlet-exclude", "[ReportService.AppHost]*",
        "--coverlet-exclude", "[ReportService.Eng.*]*",
        "--coverlet-exclude", "[ReportService.Samples.*]*",
        "--coverlet-exclude", "[ReportService.Testing]*",
        "--coverlet-exclude-by-file", "**/Program.cs",
        "--coverlet-exclude-by-file", "**/obj/**",
        // The browser session and the print itself drive a real browser; they are
        // verified by the rendering suite and the worker-level integration tests.
        "--coverlet-exclude-by-file", "**/BrowserSession.cs",
        "--coverlet-exclude-by-file", "**/ChromiumPdfPrinter.cs",
        // The PDF worker's browser probe launches a real Chromium and reports whether it
        // answers: no hermetic test can run it, and the worker's readiness check verifies it
        // in the integration test cycle.
        "--coverlet-exclude-by-file", "**/BrowserProbe.cs",
        "--coverlet-exclude-by-file", "**/BrowserHealthCheck.cs",
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
    /// Evaluates the floors. A floor reads the figure of its own test project
    /// only; the same assembly reported by another project is recorded as
    /// excluded and never compared with it. Figures are compared as exact
    /// integer ratios (covered × 100 ≥ floor × total), so a rate is never
    /// rounded up to meet a floor; the rates the report states are kept beside
    /// them.
    /// </summary>
    public static IReadOnlyList<CoverageResult> Evaluate(IReadOnlyList<AssemblyFigures> figures, IReadOnlyList<CoverageFloor> floors)
    {
        var results = new List<CoverageResult>();
        foreach (var floor in floors)
        {
            var measured = figures
                .Where(f => f.Assembly == floor.Assembly && f.Project == floor.Project)
                .ToList();
            if (measured.Count == 0)
            {
                var elsewhere = figures
                    .Where(f => f.Assembly == floor.Assembly)
                    .Select(f => f.Project)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var reason = $"absent from the reporting project's report ({floor.Project})";
                if (elsewhere.Count > 0)
                {
                    reason += $"; reported by {string.Join(", ", elsewhere)}, which is not its measuring project";
                }

                results.Add(FloorRow(floor, Fail, reason, null));
                continue;
            }

            var figure = measured[0];
            if (figure.Lines == 0)
            {
                results.Add(FloorRow(floor, NotApplicable, "no coverable line after the listed exclusions", figure));
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
                ? FloorRow(floor, Fail, string.Join("; ", reasons), figure)
                : FloorRow(floor, Pass, branchNote.Length > 0 ? branchNote : "floors met", figure));
        }

        foreach (var figure in figures)
        {
            var floor = floors.FirstOrDefault(f => f.Assembly == figure.Assembly);
            if (floor is null)
            {
                results.Add(OtherRow(figure, NoFloor, "reported, no floor (no test project)"));
            }
            else if (figure.Project != floor.Project)
            {
                results.Add(OtherRow(figure, Excluded, $"measured by {floor.Project}"));
            }
        }

        return results;
    }

    /// <summary>A percentage truncated (never rounded up) to two decimals.</summary>
    public static string Percent(int covered, int total) => total == 0
        ? "—"
        : (Math.Floor(covered * 10000m / total) / 100m).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    private static CoverageResult FloorRow(CoverageFloor floor, string result, string reason, AssemblyFigures? figure) =>
        new(
            floor.Assembly,
            floor.Project,
            result,
            reason,
            floor.LinePercent,
            floor.BranchPercent,
            figure?.Lines ?? 0,
            figure?.LinesCovered ?? 0,
            figure?.BranchPoints ?? 0,
            figure?.BranchesCovered ?? 0,
            figure is null ? "—" : Percent(figure.LinesCovered, figure.Lines),
            figure is null ? "—" : Percent(figure.BranchesCovered, figure.BranchPoints),
            figure?.LineRateAttribute,
            figure?.BranchRateAttribute,
            figure?.Report);

    private static CoverageResult OtherRow(AssemblyFigures figure, string result, string reason) =>
        new(
            figure.Assembly,
            figure.Project,
            result,
            reason,
            null,
            null,
            figure.Lines,
            figure.LinesCovered,
            figure.BranchPoints,
            figure.BranchesCovered,
            Percent(figure.LinesCovered, figure.Lines),
            Percent(figure.BranchesCovered, figure.BranchPoints),
            figure.LineRateAttribute,
            figure.BranchRateAttribute,
            figure.Report);

    [GeneratedRegex(@"\((?<covered>\d+)/(?<total>\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ConditionCoverage();
}
