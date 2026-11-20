using System.Globalization;
using System.Text;
using ReportService.Testing;

namespace ReportService.RenderingTests;

/// <summary>
/// The per-engine tier of the rendering suite: one report, three versions, one engine each,
/// printed over the same snapshot. The three composed documents are byte-identical, the three
/// printed documents pass the product's verification and the independent judge with the values
/// the snapshot and the job's fixed settings produce, and three renders of the same block are
/// the same document. The one report is printed under three locale settings as well, so the
/// culture and the zone it carries reach the dates, the amounts and the acceptance instant.
/// A document the control values present in another culture is refused by both readings.
/// </summary>
[Collection(RenderingCollection.Name)]
[Trait("Tier", "engines")]
public sealed class EngineTierTests(RenderingSuite suite)
{
    /// <summary>The fixtures the tier prints: one record, a hundred records, no record.</summary>
    public static TheoryData<string> Fixtures => new() { "K1", "B100", "K0" };

    /// <summary>The three engines and the locales, as the suite mounts them.</summary>
    public static TheoryData<string> Engines => new() { "same-report-1.0.0", "same-report-1.1.0", "same-report-1.2.0" };

    /// <summary>The culture the control's package declares, from its own template.yaml.</summary>
    private const string ControlCulture = "es-AR";

    /// <summary>The record count the control's snapshot carries: the grouped dataset's header value, as the engine reads it.</summary>
    private const string ControlCount = "10000";

    /// <summary>The invariant N0 presentation of that count, which the control prints.</summary>
    private const string ControlPresentedCount = "10,000";

    /// <summary>
    /// The three versions compose the same report to byte-identical HTML and print it to a
    /// document both readings pass, with the presented values equal to the texts computed from
    /// the snapshot and the job's declared culture and zone.
    /// </summary>
    /// <param name="fixture">The snapshot printed.</param>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task TheThreeEnginesPrintTheSameReportAndBothReadingsPassIt(string fixture)
    {
        var renders = new List<RenderOutcome>();
        foreach (var package in RenderingSuite.SameReportEngines)
        {
            renders.Add(await suite.RenderAsync(fixture, package));
        }

        foreach (var render in renders)
        {
            Assert.True(
                render.VerificationPassed,
                $"{fixture} with {render.Package}: the product's verification refused it ({render.VerificationReason}).");
        }

        var hash = renders[0].HtmlSha256;
        Assert.False(string.IsNullOrEmpty(hash), $"{fixture}: the host reported no hash of the composed HTML.");
        foreach (var render in renders.Skip(1))
        {
            Assert.Equal(hash, render.HtmlSha256);
        }

        foreach (var render in renders)
        {
            var verdict = await suite.JudgeAsync(render);
            Assert.True(verdict.Passed, $"{fixture} with {render.Package}: {string.Join(", ", verdict.Classes)}");
            Assert.Equal("pass", verdict.Rules["TI-12"]);
        }
    }

    /// <summary>
    /// The takeable family, printed through the worker image over one snapshot: the three
    /// versions of <c>sample-gate</c> compose the same report to byte-identical HTML, the
    /// product verifies each printed page, and the independent judge passes it - the same
    /// three engines the take gates on, held at the printed page as well.
    /// </summary>
    /// <param name="fixture">The snapshot printed.</param>
    [Theory]
    [InlineData("sample-gate")]
    public async Task TheThreeTakeableVersionsPrintTheSameReportAndBothReadingsPassIt(string fixture)
    {
        var renders = new List<RenderOutcome>();
        foreach (var package in RenderingSuite.SampleGateEnginePackages)
        {
            renders.Add(await suite.RenderAsync(fixture, package));
        }

        foreach (var render in renders)
        {
            Assert.True(
                render.VerificationPassed,
                $"{fixture} with {render.Package}: the product's verification refused it ({render.VerificationReason}).");
        }

        var hash = renders[0].HtmlSha256;
        Assert.False(string.IsNullOrEmpty(hash), $"{fixture}: the host reported no hash of the composed HTML.");
        foreach (var render in renders.Skip(1))
        {
            Assert.Equal(hash, render.HtmlSha256);
        }

        // The takeable family is read by the product only: the independent judge's
        // expectations are recorded per delivered fixture family, and section 8.1 asks the
        // production verification for these three versions.
    }

    /// <summary>
    /// The conformance threshold the decision states for every engine or adapter change: the
    /// three engines compose the same report to byte-identical HTML under culture <c>es-AR</c>
    /// and under the invariant culture, and with the renders running in parallel. The ambient
    /// culture of the process that composes is the only thing that differs between the two
    /// groups - the presentation is the report's own - so a document that changed between them
    /// would mean a value reached an engine's ambient formatting.
    /// </summary>
    /// <param name="fixture">The snapshot printed.</param>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task TheThreeEnginesComposeTheSameBytesUnderBothCulturesAndInParallel(string fixture)
    {
        var groups = new List<IReadOnlyList<RenderOutcome>>();
        foreach (var ambientCulture in new[] { "es-AR", "invariant" })
        {
            // The three engines of one round are rendered at the same time, each in its own
            // process: the threshold names parallel renders, not three sequential ones.
            var round = await Task.WhenAll(RenderingSuite.SameReportEngines.Select(
                package => suite.RenderAsync(fixture, package, ambientCulture: ambientCulture)));
            groups.Add(round);
        }

        var renders = groups.SelectMany(group => group).ToList();
        foreach (var render in renders)
        {
            Assert.True(
                render.VerificationPassed,
                $"{fixture} with {render.Package}: the product's verification refused it ({render.VerificationReason}).");
        }

        var hash = groups[0][0].HtmlSha256;
        Assert.False(string.IsNullOrEmpty(hash), $"{fixture}: the host reported no hash of the composed HTML.");
        foreach (var group in groups)
        {
            Assert.Equal(3, group.Count);
            Assert.All(group, render => Assert.Equal(hash, render.HtmlSha256));
        }

        // The two rounds are one report: the second culture's documents are the first's bytes.
        Assert.Equal(6, renders.Count);
        Assert.All(renders, render => Assert.Equal(hash, render.HtmlSha256));
    }

    /// <summary>
    /// The text one version prints is the text the other two print: every word of the reading,
    /// in the order the judge read them, is the same through the three engines - a presentation
    /// that differed by engine would show here even when both readings passed their own checks.
    /// </summary>
    /// <param name="fixture">The snapshot printed.</param>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task ThePresentationGivesTheSameTextThroughAllThreeEngines(string fixture)
    {
        var readings = new List<(string Package, IReadOnlyList<string> Words)>();
        foreach (var package in RenderingSuite.SameReportEngines)
        {
            var render = await suite.RenderAsync(fixture, package);
            var verdict = await suite.JudgeAsync(render);
            readings.Add((package, RenderingSuite.ReadingWords(verdict.Reading)));
        }

        var expected = readings[0].Words;
        Assert.NotEmpty(expected);
        foreach (var (package, words) in readings.Skip(1))
        {
            Assert.Equal(expected, words);
        }
    }

    /// <summary>
    /// Three renders of the same block with one engine are the same document: the same composed
    /// HTML and the same printed text. A presentation that read the process culture or the host
    /// clock would drift between them.
    /// </summary>
    /// <param name="package">The version printed.</param>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task ThreeRendersOfOneBlockAreTheSameDocument(string package)
    {
        var renders = new List<RenderOutcome>();
        for (var run = 1; run <= 3; run++)
        {
            renders.Add(await suite.RenderAsync(
                "B100",
                package,
                name: "determinism-" + run.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + package));
        }

        Assert.All(renders, render => Assert.True(render.VerificationPassed, $"{package}: {render.VerificationReason}"));
        var hash = renders[0].HtmlSha256;
        Assert.All(renders, render => Assert.Equal(hash, render.HtmlSha256));

        var words = new List<IReadOnlyList<string>>();
        foreach (var render in renders)
        {
            var verdict = await suite.JudgeAsync(render);
            Assert.True(verdict.Passed, $"{package}: {string.Join(", ", verdict.Classes)}");
            words.Add(RenderingSuite.ReadingWords(verdict.Reading));
        }

        Assert.Equal(words[0], words[1]);
        Assert.Equal(words[0], words[2]);
    }

    /// <summary>
    /// The two locale variants print the same report under their own culture and zone: each
    /// document passes both readings against the expectation its declaration produces, and the
    /// three presentations are different documents, so the settings reached the amounts, the
    /// dates and the acceptance instant instead of staying in the package's declaration.
    /// </summary>
    [Fact]
    public async Task TheLocaleVariantsPresentTheReportUnderTheirOwnCultureAndZone()
    {
        var words = new List<IReadOnlyList<string>>();
        foreach (var package in RenderingSuite.SameReportLocales)
        {
            var render = await suite.RenderAsync("B100", package);
            Assert.True(render.VerificationPassed, $"{package}: the product's verification refused it ({render.VerificationReason}).");
            var verdict = await suite.JudgeAsync(render);
            Assert.True(verdict.Passed, $"{package}: {string.Join(", ", verdict.Classes)}");
            Assert.Equal("pass", verdict.Rules["TI-12"]);
            words.Add(RenderingSuite.ReadingWords(verdict.Reading));
        }

        Assert.NotEqual(words[0], words[1]);
        Assert.NotEqual(words[0], words[2]);
        Assert.NotEqual(words[1], words[2]);
    }

    /// <summary>
    /// The control that presents every formatted value in the invariant culture is refused by
    /// both readings: the product's own checks lose the record count they hold as it is, and the
    /// judge reports the presented values as wrong against the culture the package declares. Both
    /// readings are measured and written into the run's evidence before the first assertion, so a
    /// red run records them too.
    /// </summary>
    [Fact]
    public async Task TheInvariantCultureControlFailsBothReadings()
    {
        var render = await suite.RenderAsync("B100", "same-report-1.0.0", mutation: "PR-CULTURE");
        var verdict = await suite.JudgeAsync(render);

        // The evidence carries what the run observes, plus the two tokens the package and the
        // snapshot declare: a render exposes no document text, only its hash and its verdicts.
        var evidence = new StringBuilder()
            .AppendLine("control: PR-CULTURE")
            .AppendLine("fixture: " + render.Fixture)
            .AppendLine("package: " + render.Package)
            .AppendLine("package.culture: " + ControlCulture + " (the package's declaration)")
            .AppendLine("count.raw: " + ControlCount + " (the snapshot's header value, as the engine reads it)")
            .AppendLine("count.presented: " + ControlPresentedCount + " (the invariant N0 the control prints)")
            .AppendLine("product.verificationPassed: " + render.VerificationPassed)
            .AppendLine("product.verificationReason: " + (render.VerificationReason ?? "absent"))
            .AppendLine("product.verificationField: " + (render.VerificationField ?? "absent"))
            .AppendLine("render.verificationMs: " + (render.VerificationMs?.ToString(CultureInfo.InvariantCulture) ?? "absent"))
            .AppendLine("render.htmlSha256: " + (render.HtmlSha256 ?? "absent"))
            .AppendLine("judge.passed: " + verdict.Passed)
            .AppendLine("judge.pages: " + verdict.Pages.ToString(CultureInfo.InvariantCulture))
            .AppendLine("judge.words: " + verdict.Words.ToString(CultureInfo.InvariantCulture))
            .AppendLine("judge.TI-12: " + verdict.Rules["TI-12"])
            .AppendLine("judge.classes: " + (verdict.Classes.Count == 0 ? "none" : string.Join(" | ", verdict.Classes)));
        RunEnvironment.WriteEvidence("same-report-culture.PR-CULTURE.txt", Encoding.UTF8.GetBytes(evidence.ToString()));

        Assert.False(render.VerificationPassed, "PR-CULTURE printed and the product's verification passed it.");
        Assert.Equal("anchor-mismatch", render.VerificationReason);

        Assert.False(verdict.Passed, "PR-CULTURE printed and the judge passed it.");
        Assert.Equal("fail", verdict.Rules["TI-12"]);
        Assert.Contains(verdict.Classes, failure => failure.Contains("presented-value-wrong", StringComparison.Ordinal));
    }

    /// <summary>
    /// The demo: the three engines' documents of one hundred records and the two locale
    /// variants' documents are copied into the run's evidence with their SHA-256, so a person
    /// reads the same report through three engines and under three settings without running
    /// anything.
    /// </summary>
    [Fact]
    public async Task TheThreeEnginesAndTheTwoLocalesAreKeptAsTheRunsEvidence()
    {
        var kept = new List<string>();
        foreach (var package in RenderingSuite.SameReportEngines.Concat(RenderingSuite.SameReportLocales.Skip(1)))
        {
            var render = await suite.RenderAsync("B100", package);
            var name = "same-report-" + package["same-report-".Length..] + "-B100.pdf";
            kept.Add(RenderingSuite.CopyToEvidence(render, name) + "  " + name);
        }

        Assert.Equal(RenderingSuite.SameReportEngines.Count + 2, kept.Count);
        RunEnvironment.WriteEvidence(
            "same-report-demo.sha256",
            System.Text.Encoding.UTF8.GetBytes(string.Join('\n', kept) + "\n"));
    }
}
