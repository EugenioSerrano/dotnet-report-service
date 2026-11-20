using System.Globalization;
using System.Text;
using System.Text.Json;
using ReportService.Pdf.Verification;
using ReportService.Testing;
using ReportService.Testing.Fixtures;
using ReportService.Testing.Reading;

namespace ReportService.RenderingTests;

/// <summary>
/// The rendered case at the bound: the three at-the-bound fixtures are printed with
/// the shipped simple sample, the product's own verification and the independent
/// judge both pass them, the recording reads the field back against its source, and
/// the test-only control at the same size makes the verification fail. The page count,
/// the print and verification times and the wall time are measured and written beside
/// the run, never assumed.
/// </summary>
[Collection(RenderingCollection.Name)]
public sealed class RowAtTheBoundTests(RenderingSuite suite)
{
    private static readonly string[] PilotHeadRow = ["#", "Description", "Quantity", "Unit price", "Amount"];

    private const double PointsPerMillimetre = PlacementConvention.PointsPerMillimetre;

    private const double DescriptionColumnStartMm = 49.2;

    private const double DescriptionColumnEndMm = 86.4;

    /// <summary>The three at-the-bound fixtures and the normalised length of their field.</summary>
    public static TheoryData<string, int> AtTheBound => new()
    {
        { BoundFamily.Ascii, 226_440 },
        { BoundFamily.TwoByte, 113_220 },
        { BoundFamily.ThreeByte, 75_480 },
    };

    [Theory]
    [MemberData(nameof(AtTheBound))]
    public async Task TheRowAtTheBoundPrintsEveryCharacter(string fixture, int letters)
    {
        var render = await suite.RenderAsync(fixture, "pilot-simple");
        Assert.True(
            render.VerificationPassed,
            $"{fixture}: the product's verification refused the document ({render.VerificationReason}).");
        Assert.NotNull(render.Pdf);

        var reading = PrintedFieldText.Read(
            render.Pdf!,
            15 * PointsPerMillimetre,
            12 * PointsPerMillimetre,
            18 * PointsPerMillimetre,
            12 * PointsPerMillimetre,
            PilotHeadRow,
            DescriptionColumnStartMm * PointsPerMillimetre,
            DescriptionColumnEndMm * PointsPerMillimetre);
        var match = reading.Find(BoundFamily.Description(fixture));

        Assert.True(match.Passed, $"{fixture}: {match.Difference} at {match.Position} (page {match.Page}).");
        Assert.Equal(letters, match.Length);
        Assert.True(match.LastPage > match.FirstPage, $"{fixture}: the row printed on one page.");
        Assert.True(reading.Pages.All(page => page.Words.Count > 0), $"{fixture}: a page printed no word.");
        Assert.DoesNotContain("(continues)", reading.Text, StringComparison.Ordinal);

        var verdict = await suite.JudgeAsync(render);
        Assert.True(verdict.Passed, $"{fixture} judged: {string.Join(", ", verdict.Classes)}");
        Assert.Equal("pass", verdict.Rules["TI-4"]);
        Assert.Equal("pass", verdict.Rules["TI-7"]);

        WriteResult(fixture, render, verdict, match);
    }

    [Fact]
    public async Task TheVerificationRefusesOneDroppedCharacterAtTheBound()
    {
        var render = await suite.RenderAsync(BoundFamily.Ascii, "pilot-simple", "BoundCharDropped");
        Assert.False(render.VerificationPassed);
        Assert.Equal(VerificationReasons.TextDropped, render.VerificationReason);
        Assert.NotNull(render.Pdf);

        var verdict = await suite.JudgeAsync(render);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Classes, failure => failure.Contains("text-dropped", StringComparison.Ordinal));

        WriteControlResult(render, verdict);
    }

    private static void WriteResult(string fixture, RenderOutcome render, Verdict verdict, FieldMatch match)
    {
        if (RunEnvironment.RunDirectory is null)
        {
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(render.Pdf!, ".json")));
        var root = document.RootElement;
        var text = new StringBuilder()
            .AppendLine($"fixture={fixture}")
            .AppendLine($"pages={root.GetProperty("pages").GetInt32()}")
            .AppendLine($"words={root.GetProperty("words").GetInt32()}")
            .AppendLine($"durationMs={root.GetProperty("durationMs").GetInt64()}")
            .AppendLine($"verificationMs={MeasuredLong(root, "verificationMs")}")
            .AppendLine($"matched={match.Length}")
            .AppendLine($"firstPage={match.FirstPage}")
            .AppendLine($"lastPage={match.LastPage}")
            .AppendLine($"verification={render.VerificationPassed}")
            .AppendLine($"judgePages={verdict.Pages}")
            .AppendLine($"judgeWords={verdict.Words}")
            .ToString();
        RunEnvironment.WriteEvidence($"{fixture}.reading.txt", Encoding.UTF8.GetBytes(text));
    }

    private static void WriteControlResult(RenderOutcome render, Verdict verdict)
    {
        if (RunEnvironment.RunDirectory is null || render.Pdf is null)
        {
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(render.Pdf, ".json")));
        var root = document.RootElement;
        var text = new StringBuilder()
            .AppendLine("mutation=BoundCharDropped")
            .AppendLine($"verification={render.VerificationPassed}")
            .AppendLine($"reason={MeasuredString(root, "verificationReason")}")
            .AppendLine($"page={MeasuredLong(root, "verificationPage")}")
            .AppendLine($"unit={MeasuredLong(root, "verificationUnit")}")
            .AppendLine($"offset={MeasuredLong(root, "verificationOffset")}")
            .AppendLine($"verificationMs={MeasuredLong(root, "verificationMs")}")
            .AppendLine($"classes={string.Join(",", verdict.Classes)}")
            .ToString();
        RunEnvironment.WriteEvidence("boundAscii.control.txt", Encoding.UTF8.GetBytes(text));
    }

    private static string MeasuredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : "null";

    private static string MeasuredLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64().ToString(CultureInfo.InvariantCulture)
            : "null";
}
