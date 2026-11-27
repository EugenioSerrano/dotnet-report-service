using ReportService.Pdf.Verification.PageCodes;
using ReportService.Testing.PageCodeFixtureDependency;

namespace ReportService.Testing.PageCodeFixture;

/// <summary>A generator whose drawing comes from its private dependency.</summary>
public sealed class FixturePageCodeGenerator : IPageCodeGenerator
{
    /// <summary>The name a template declares.</summary>
    public const string GeneratorName = "fixture-cells";

    /// <inheritdoc />
    public string Name => GeneratorName;

    /// <inheritdoc />
    public PageCodeCheck Validate(IReadOnlyDictionary<string, string> options, PageCodeArea area, int maxValueBytes) =>
        PageCodeCheck.Accept(FixtureCells.Draw(new string('~', 16), area.WidthPt, area.HeightPt));

    /// <inheritdoc />
    public PageCodeContent Generate(string value, IReadOnlyDictionary<string, string> options, PageCodeArea area) =>
        PageCodeContent.Of(FixtureCells.Draw(value, area.WidthPt, area.HeightPt));
}
