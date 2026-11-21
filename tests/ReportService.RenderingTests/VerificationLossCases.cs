using System.Globalization;
using System.Text;
using ReportService.Testing;
using ReportService.Testing.VerificationLoss;

namespace ReportService.RenderingTests;

/// <summary>
/// The known-bad documents that survive the take-time checks: the verification-loss corpus is
/// printed by this tier's own host and judged by the independent reader, and the two readings are
/// asserted apart. The production verification reports the one reason the corpus declares - the
/// first failing check, so a document carrying two data defects reports the source check - and the
/// judge's class set must contain the class the corpus maps that reason onto: a class set that does
/// not is a disagreement and fails the suite. The honest document passes both readings, and the
/// supplementary template-authored control is refused by both, its blank trailing page included.
/// The tier has no job, output or artifact, so the publication of the honest document is the
/// job-level class's evidence, never this one's.
/// </summary>
[Collection(RenderingCollection.Name)]
public sealed class VerificationLossCases(RenderingSuite suite)
{
    /// <summary>The package every job case prints, and the one the loss documents are printed with here.</summary>
    private const string Package = VerificationLossCorpus.Template;

    /// <summary>The supplementary package only this tier prints.</summary>
    private const string Supplementary = VerificationLossCorpus.SupplementaryTemplate;

    /// <summary>The file the committed corpus is frozen by.</summary>
    private static string CommittedSums => Path.Combine(
        VerificationLossCorpus.PackagesDirectory,
        "..",
        "snapshots",
        VerificationLossCorpus.SumsFileName);

    /// <summary>Each loss document, the one reason the product's verification reports and the judge's classes.</summary>
    public static TheoryData<string, string, string[]> Losses => new()
    {
        // A value holding a character the browser does not paint: the printed text loses it.
        { VerificationLossCorpus.TextLoss, "text-dropped", ["text-dropped"] },
        // A row that composes no text at all: the source check stops the block before printing.
        { VerificationLossCorpus.RowLoss, "row-without-text", ["row-lost"] },
        // Both data defects in one document: the source check reports the empty row, and the
        // judge reads the PDF and reports both the lost row and the dropped characters.
        { VerificationLossCorpus.ManyLoss, "row-without-text", ["row-lost", "text-dropped"] },
    };

    /// <summary>
    /// A loss document is caught by both readings: the product reports exactly its one reason and
    /// the judge's class set contains the class that reason maps onto.
    /// </summary>
    /// <param name="document">The corpus document printed.</param>
    /// <param name="reason">The one reason the product's verification reports for it.</param>
    /// <param name="classes">The judge's classes it must contain at least.</param>
    [Theory]
    [MemberData(nameof(Losses))]
    public async Task ALossDocumentIsRefusedByBothReadings(string document, string reason, string[] classes)
    {
        var render = await suite.RenderAsync(RenderingSuite.CorpusFixture(document), Package, name: document + "-" + Package);
        Assert.False(render.VerificationPassed, $"{document}: the product's verification passed a known-bad document");
        Assert.Equal(reason, render.VerificationReason);
        Assert.NotNull(render.Pdf);

        var verdict = await suite.JudgeAsync(render);
        Assert.False(verdict.Passed, $"{document}: the judge passed a known-bad document");
        foreach (var expected in classes)
        {
            Assert.True(
                verdict.Classes.Any(failure => failure.Contains(expected, StringComparison.Ordinal)),
                $"{document}: the judge reported {string.Join(", ", verdict.Classes)}; expected it to contain {expected}.");
        }

        // The dropped text is the description's, compared with its own record by the row-text
        // rule - a class set produced by rows the judge could not classify carries no such
        // failure, which is the disagreement this assertion exists to catch.
        if (classes.Contains("text-dropped", StringComparer.Ordinal))
        {
            Assert.True(
                verdict.Classes.Any(failure =>
                    failure.StartsWith("TI-3", StringComparison.Ordinal)
                    && failure.Contains("text-dropped", StringComparison.Ordinal)),
                $"{document}: the dropped text was not reported by the row-text rule: {string.Join(", ", verdict.Classes)}");
        }

        Keep(document, render, verdict);
    }

    /// <summary>
    /// The honest document of the corpus passes both readings: it is the positive half of the
    /// tier's controls, and a control that passes on a wrong document fails the suite.
    /// </summary>
    [Fact]
    public async Task TheHonestDocumentPassesBothReadings()
    {
        var render = await suite.RenderAsync(RenderingSuite.CorpusFixture(VerificationLossCorpus.Honest), Package, name: VerificationLossCorpus.Honest + "-" + Package);
        Assert.True(
            render.VerificationPassed,
            $"the honest document was refused by the product's verification ({render.VerificationReason})");
        Assert.Null(render.VerificationReason);

        var verdict = await suite.JudgeAsync(render);
        Assert.True(verdict.Passed, $"the honest document was refused by the judge: {string.Join(", ", verdict.Classes)}");
        Assert.True(verdict.Pages >= 1, "the honest document printed no page.");

        // Every record found its row and every declared column was compared: the rows are
        // classified, so the row-text and amount rules ran on this document rather than
        // passing because nothing was read.
        Assert.Equal("pass", verdict.Rules["TI-2"]);
        Assert.Equal("pass", verdict.Rules["TI-3"]);
        Assert.Equal("pass", verdict.Rules["TI-4"]);

        Keep(VerificationLossCorpus.Honest, render, verdict);
    }

    /// <summary>
    /// The supplementary template-authored control is refused by both readings: the product's
    /// source check reports the row whose text was omitted, and the judge reads the lost row and
    /// the blank trailing page - the only control that carries the empty-page check, because no
    /// honest, takeable template produces one.
    /// </summary>
    [Fact]
    public async Task TheSupplementaryControlIsRefusedByBothReadings()
    {
        var render = await suite.RenderAsync(RenderingSuite.CorpusFixture(VerificationLossCorpus.Honest), Supplementary, name: VerificationLossCorpus.Honest + "-" + Supplementary);
        Assert.False(render.VerificationPassed, "the supplementary control was passed by the product's verification");
        Assert.Equal("row-without-text", render.VerificationReason);

        var verdict = await suite.JudgeAsync(render);
        Assert.False(verdict.Passed, "the supplementary control was passed by the judge");

        // Two and only two failures: the row the page does not print and the blank trailing
        // page. A set that "contains row-lost" because every row was unclassifiable is the
        // artefact this assertion replaces, so the record and the page are named.
        Assert.Equal(2, verdict.Classes.Count);
        var lost = Assert.Single(verdict.Classes, failure => failure.Contains("row-lost", StringComparison.Ordinal));
        Assert.StartsWith("TI-2", lost, StringComparison.Ordinal);
        Assert.Contains("row=2", lost, StringComparison.Ordinal);
        var structure = Assert.Single(verdict.Classes, failure => failure.Contains("structure-lost", StringComparison.Ordinal));
        Assert.StartsWith("TI-1", structure, StringComparison.Ordinal);
        Assert.Contains($"page={verdict.Pages}", structure, StringComparison.Ordinal);
        Assert.DoesNotContain(verdict.Classes, failure => failure.Contains("row=1", StringComparison.Ordinal) || failure.Contains("row=3", StringComparison.Ordinal));

        // The other two records are read and compared: their descriptions print, so the lost
        // row is the record the page omits and not an artefact of unread rows.
        var reading = string.Join(' ', RenderingSuite.ReadingWords(verdict.Reading));
        Assert.Contains("Annual inspection", reading, StringComparison.Ordinal);
        Assert.Contains("Monthly audit", reading, StringComparison.Ordinal);

        Keep(Supplementary, render, verdict);
    }

    /// <summary>
    /// The corpus is frozen where this tier prints it: every snapshot it renders has the SHA-256
    /// the committed sums record, so a later change to the corpus is visible here before a case
    /// asserts a verdict on it.
    /// </summary>
    [Fact]
    public void TheCorpusIsFrozenByItsCommittedHash()
    {
        var sums = File.ReadAllLines(CommittedSums)
            .Where(line => line.Trim().Length > 0)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[^1], parts => parts[0], StringComparer.Ordinal);

        foreach (var snapshot in VerificationLossCorpus.Snapshots())
        {
            Assert.True(sums.TryGetValue(snapshot.FileName, out var recorded), $"{snapshot.FileName} is not in {CommittedSums}");
            Assert.Equal(recorded, snapshot.Sha256);
        }
    }

    // The measured verdict of one document, kept with the run: the production reason and the
    // judge's classes, as the two readings produced them.
    private static void Keep(string document, RenderOutcome render, Verdict verdict)
    {
        if (RunEnvironment.RunDirectory is null)
        {
            return;
        }

        var text = new StringBuilder()
            .AppendLine($"document={document}")
            .AppendLine($"package={render.Package}")
            .AppendLine($"productionPassed={render.VerificationPassed}")
            .AppendLine($"productionReason={render.VerificationReason ?? "null"}")
            .AppendLine($"judgePassed={verdict.Passed}")
            .AppendLine($"judgePages={verdict.Pages.ToString(CultureInfo.InvariantCulture)}")
            .AppendLine($"judgeClasses={string.Join(",", verdict.Classes)}")
            .ToString();
        RunEnvironment.WriteEvidence("verification-loss." + document + ".txt", Encoding.UTF8.GetBytes(text));
    }
}
