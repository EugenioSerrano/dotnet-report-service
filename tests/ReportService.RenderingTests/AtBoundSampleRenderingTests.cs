using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReportService.Data;
using ReportService.Pdf.Verification;
using ReportService.Testing;
using ReportService.Testing.TemplatePackages;
using ReportService.TemplateChecks.Sample;
using ReportService.TemplateSource.Take;
using ReportService.Workers.Pdf.Templates;

namespace ReportService.RenderingTests;

/// <summary>
/// The at-bound sample of the control package, printed the way the take prints it and read
/// by the product's own verification with its anchored field compared against the source:
/// the document is the one <see cref="AtBoundSample"/> derives from the package, printed by
/// the rendering host image on the PDF base through the sample port, and the verification
/// passes only if every character of the record at the bound reached the page.
/// <para>
/// The known-bad control is applied to the composed HTML <b>after</b> the expected units are
/// extracted, so the reference stays whole and the page prints one character fewer: the
/// product's verification must answer <c>text-dropped</c> and the independent judge must
/// report the same class. A control that does not fail would make this evidence vacuous
/// exactly where it matters.
/// </para>
/// <para>
/// The same class measures the take's three renders - the sample, the at-bound sample and
/// the empty sample - three repetitions each, and holds their figures to the deadline
/// arithmetic: the delivered render budget of 20 s sits inside the 22 s the arithmetic
/// allows, and the whole take stays below its own 240 s and the activity's 300 s.
/// </para>
/// </summary>
[Collection(RenderingCollection.Name)]
public sealed class AtBoundSampleRenderingTests(RenderingSuite suite) : IDisposable
{
    private const string Template = AtBoundFixtures.TemplateId;

    private const string Version = AtBoundFixtures.Version;

    /// <summary>The number of times each render is measured.</summary>
    private const int Repetitions = 3;

    /// <summary>The source and manifest read's planned bound, in seconds.</summary>
    private const int SourceAndManifestPlanSeconds = 30;

    /// <summary>The static checks' share of the take, in seconds.</summary>
    private const int StaticChecksSeconds = 30;

    /// <summary>The verification's planned bound, in seconds.</summary>
    private const int VerificationPlanSeconds = 5;

    /// <summary>The margin the arithmetic keeps, in seconds.</summary>
    private const int MarginSeconds = 30;

    /// <summary>The package the rendering host mounts for the audit render.</summary>
    private const string Package = "invoice-at-bound";

    /// <summary>The control's fixture: an ordinary record, then the record at the bound.</summary>
    private const string AuditFixture = "at-bound-audit";

    private static readonly JsonSerializerOptions Writing = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string root = Directory.CreateTempSubdirectory("at-bound-rendering-").FullName;

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder is not a test failure.
        }
    }

    [Fact]
    public async Task TheAtBoundSampleOfTheControlPackagePrintsEveryCharacterAndVerifies()
    {
        var files = AtBoundFixtures.Package();
        var derived = AtBoundSamples.Derive(files, Template, Version);

        // The derivation the take runs answers the anchored position: the manifest's item
        // anchor is compared with the source by the verification, so the field is the one the
        // control declares as printed as it is.
        Assert.True(derived.Result.Applies);
        Assert.True(derived.Result.AnchorBound);
        Assert.Equal("$.description", derived.Result.Chosen!.Path);
        AssertBoundValue(derived.Result.Document!);

        var folder = WritePackage(root, "at-bound-sample", AtBoundSamples.WithSample(files, derived.Result.Document!));
        var result = await suite.SampleAsync(folder, "at-bound-sample");

        // The product's verification accepted the print: C-2 holds every printed text against
        // the composed one, C-4 holds the anchored field against the source, and C-5 found a
        // word on every page.
        Assert.Equal("rendered", result.Outcome);
        Assert.Null(result.FailureKind);
        Assert.Null(result.FailureReason);
        Assert.True(result.Pages > 1, $"the record at the bound printed on {result.Pages} page(s).");
        Assert.True(result.Words > 0);
        Assert.NotNull(result.HtmlSha256);

        WriteEvidence("at-bound-sample.txt", new StringBuilder()
            .AppendLine($"template={Template}")
            .AppendLine($"version={Version}")
            .AppendLine($"position={derived.Result.Chosen.Path}")
            .AppendLine($"shape={derived.Result.Chosen.Shape}")
            .AppendLine($"derivation={derived.Result.Derivation}")
            .AppendLine($"anchorBound={derived.Result.AnchorBound}")
            .AppendLine($"recordBytes={derived.Result.RecordBytes}")
            .AppendLine($"valueSha256={derived.Result.ValueSha256}")
            .AppendLine($"pages={result.Pages}")
            .AppendLine($"words={result.Words}")
            .AppendLine($"composeMs={result.ComposeMs.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"printMs={result.PrintMs.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"verifyMs={result.VerifyMs.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine("processTreePeak=not measured (the delivered host reports none)")
            .ToString());
    }

    [Theory]
    [InlineData("handlebars", "html/row.hbs")]
    [InlineData("liquid", "html/row.liquid")]
    public void TheControlPackageOfAnotherEngineIsNotDelivered(string engine, string rowPart)
    {
        // The control package the phase delivered is written in one engine's syntax. A
        // counterpart for another engine would be a second package with its own parts; none
        // is delivered, so the property is asserted on the one control package and the gap is
        // named here rather than left to an argument.
        Assert.Skip(
            $"the at-bound control package is delivered in scriban only; no {engine} counterpart exists "
            + $"(its row part would be {rowPart}), so the printing property is asserted on the one delivered control package.");
    }

    [Fact]
    public async Task TheKnownBadControlAtTheBoundIsRefusedByTheProductAndByTheJudge()
    {
        var fixture = PrepareAuditRender();
        var render = await suite.RenderAsync(AuditFixture, Package, "BoundCharDropped", name: "at-bound-audit-dropped");

        // The control prints a document - the evidence exists because the print produced one -
        // and it removes one character from the composed HTML after the expected units were
        // extracted, so the reference is whole and only the page is short by one character.
        Assert.NotNull(render.Pdf);
        Assert.False(render.VerificationPassed, "the product accepted a page that lost a character at the bound.");
        Assert.Equal(VerificationReasons.TextDropped, render.VerificationReason);

        var verdict = await suite.JudgeAsync(render);
        Assert.False(verdict.Passed, "the judge accepted a document that lost a character at the bound.");
        Assert.Contains(verdict.Classes, failure => failure.Contains("text-dropped", StringComparison.Ordinal));

        WriteEvidence("at-bound-control.txt", new StringBuilder()
            .AppendLine($"fixture={fixture}")
            .AppendLine("mutation=BoundCharDropped")
            .AppendLine($"verification={render.VerificationPassed}")
            .AppendLine($"reason={render.VerificationReason}")
            .AppendLine($"classes={string.Join(",", verdict.Classes)}")
            .AppendLine($"judgePages={verdict.Pages}")
            .AppendLine($"judgeWords={verdict.Words}")
            .AppendLine($"verificationMs={render.VerificationMs?.ToString(CultureInfo.InvariantCulture) ?? "null"}")
            .ToString());
    }

    [Fact]
    public async Task TheNoRecordControlPrintsTheContractSynthesisedRecordAtTheBound()
    {
        var files = AtBoundFixtures.Package(records: []);
        var derived = AtBoundSamples.Derive(files, Template, Version);

        // A legal document with no record: the derivation synthesises the record from the
        // contract, the gate runs on it, and nothing about the package is refused.
        Assert.True(derived.Result.Applies);
        Assert.Equal(AtBoundDerivation.Contract, derived.Result.Derivation);
        AssertBoundValue(derived.Result.Document!);

        var folder = WritePackage(root, "at-bound-no-record", AtBoundSamples.WithSample(files, derived.Result.Document!));
        var result = await suite.SampleAsync(folder, "at-bound-no-record");

        Assert.Equal("rendered", result.Outcome);
        Assert.Null(result.FailureReason);
        Assert.True(result.Pages > 1, $"the contract-synthesised record printed on {result.Pages} page(s).");

        WriteEvidence("at-bound-no-record.txt", new StringBuilder()
            .AppendLine($"derivation={derived.Result.Derivation}")
            .AppendLine($"position={derived.Result.Chosen!.Path}")
            .AppendLine($"pages={result.Pages}")
            .AppendLine($"words={result.Words}")
            .AppendLine($"composeMs={result.ComposeMs.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"printMs={result.PrintMs.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"verifyMs={result.VerifyMs.ToString("0.0", CultureInfo.InvariantCulture)}")
            .ToString());
    }

    [Theory]
    [InlineData(
        """{ "type": "object", "additionalProperties": false, "properties": { "description": { "type": "string", "maxLength": 65536 } } }""",
        "maxLength 65536")]
    [InlineData(
        """{ "type": "object", "properties": { "description": { "type": "string" } }, "not": { "properties": { "description": { "type": "string" } }, "required": ["description"] } }""",
        "not")]
    [InlineData(
        """{ "type": "object", "properties": { "description": { "type": "string", "format": "uuid" } } }""",
        "format uuid")]
    public void AContractThatAdmitsTheBoundNowhereSpendsNoAtBoundRender(string recordSchema, string cap)
    {
        var derived = AtBoundSamples.Derive(AtBoundFixtures.Package(recordSchema: recordSchema), Template, Version);

        // The take records at-bound-not-applicable and takes the version; this tier spends no
        // render on it, which is what keeps the deadline arithmetic honest.
        Assert.False(derived.Result.Applies);
        Assert.Null(derived.Result.Document);
        Assert.Null(derived.Result.Chosen);
        Assert.NotNull(derived.Result.NotApplicable);
        Assert.Contains(cap, derived.Result.NotApplicable!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheThreeTakeRendersFitTheDeadlineAtTheDeliveredBudget()
    {
        var files = AtBoundFixtures.Package();
        var derived = AtBoundSamples.Derive(files, Template, Version);
        Assert.True(derived.Result.Applies);

        var sampleFolder = WritePackage(root, "measure-sample", files);
        var boundFolder = WritePackage(root, "measure-bound", AtBoundSamples.WithSample(files, derived.Result.Document!));

        // The three renders the take runs, three repetitions each, through the one port the
        // worker uses: the sample, the at-bound sample and the empty sample. The empty one is
        // the control package's own sample with zero records - the document the take's second
        // call renders - so the measurement reads the same verb for all three.
        var emptyFolder = WritePackage(root, "measure-empty", AtBoundFixtures.Package(records: []));
        var samples = new List<SampleRenderOutcome>();
        var bounds = new List<SampleRenderOutcome>();
        var empties = new List<SampleRenderOutcome>();
        for (var repetition = 1; repetition <= Repetitions; repetition++)
        {
            samples.Add(await suite.SampleAsync(sampleFolder, $"measure-sample-{repetition}"));
            bounds.Add(await suite.SampleAsync(boundFolder, $"measure-bound-{repetition}"));
            empties.Add(await suite.SampleAsync(emptyFolder, $"measure-empty-{repetition}"));
        }

        Assert.All(samples.Concat(bounds).Concat(empties), result => Assert.Equal("rendered", result.Outcome));

        var budget = new SampleRenderOptions().RenderBudgetSeconds;
        var maximumBudget = SampleRenderOptions.MaxRenderBudgetSeconds;
        var deadline = TemplateTake.Deadline;
        var startToClose = TakeTemplateVersionActivityOptions.StartToClose;

        // The delivered budget is 20 s and the arithmetic admits at most 22 s: the value sits
        // inside the bound and neither the option nor the worker's configuration moves.
        Assert.Equal(20, budget);
        Assert.Equal(22, maximumBudget);

        // Every render call - composition and print alike - finishes inside the budget handed
        // to it, and one verification inside its own 5 s bound.
        var calls = samples.Concat(bounds).Concat(empties)
            .SelectMany(result => new[] { result.ComposeMs, result.PrintMs })
            .ToList();
        var worstCall = calls.Max();
        var worstVerification = samples.Concat(bounds).Concat(empties).Max(result => result.VerifyMs);
        Assert.True(worstCall <= budget * 1000, $"a render call took {worstCall:0.0} ms, above the {budget} s budget.");
        Assert.True(worstVerification <= VerificationPlanSeconds * 1000, $"a verification took {worstVerification:0.0} ms.");

        // The worst render of each kind and their sum, the figures the deadline arithmetic
        // records. A render is one composition, one print and one verification.
        var worstSample = samples.Max(result => result.ComposeMs + result.PrintMs + result.VerifyMs);
        var worstBound = bounds.Max(result => result.ComposeMs + result.PrintMs + result.VerifyMs);
        var worstEmpty = empties.Max(result => result.ComposeMs + result.PrintMs + result.VerifyMs);
        var worstSum = worstSample + worstBound + worstEmpty;

        // The arithmetic: S + the static checks + six render calls at the budget + three
        // verifications + the margin fit the take's deadline, which is below the activity's.
        var boundTotal = SourceAndManifestPlanSeconds + StaticChecksSeconds
            + (6 * budget) + (3 * VerificationPlanSeconds) + MarginSeconds;
        Assert.True(boundTotal <= deadline.TotalSeconds, $"the bound's arithmetic totals {boundTotal} s, above {deadline.TotalSeconds} s.");
        Assert.True(deadline < startToClose, "the take's deadline must stay below the activity's start-to-close.");
        var measuredTotal = SourceAndManifestPlanSeconds + StaticChecksSeconds
            + (worstSum / 1000) + (3 * VerificationPlanSeconds) + MarginSeconds;
        Assert.True(measuredTotal <= deadline.TotalSeconds, $"the measured worst case totals {measuredTotal:0.0} s, above {deadline.TotalSeconds} s.");
        var measuredRendersSeconds = worstSum / 1000;

        WriteEvidence("take-deadline.txt", new StringBuilder()
            .AppendLine("template=invoice-at-bound")
            .AppendLine("renders=sample,at-bound,empty")
            .AppendLine($"repetitions={Repetitions}")
            .AppendLine($"referenceEnvironment=docs/reference-environment.md revision 5")
            .AppendLine($"deadlineSeconds={deadline.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}")
            .AppendLine($"startToCloseSeconds={startToClose.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}")
            .AppendLine($"renderBudgetSeconds={budget}")
            .AppendLine($"maxRenderBudgetSeconds={maximumBudget}")
            .AppendLine($"worstSampleMs={worstSample.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"worstAtBoundMs={worstBound.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"worstEmptyMs={worstEmpty.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"worstSumMs={worstSum.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"worstCallMs={worstCall.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"worstVerificationMs={worstVerification.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"pagesAtBound={string.Join(",", bounds.Select(result => result.Pages.ToString(CultureInfo.InvariantCulture)))}")
            .AppendLine($"arithmeticBound={SourceAndManifestPlanSeconds}+{StaticChecksSeconds}+6*{budget}+3*{VerificationPlanSeconds}+{MarginSeconds}={boundTotal} <= {deadline.TotalSeconds} < {startToClose.TotalSeconds}")
            .AppendLine($"arithmeticMeasured={SourceAndManifestPlanSeconds}+{StaticChecksSeconds}+{measuredRendersSeconds.ToString("0.0", CultureInfo.InvariantCulture)}+3*{VerificationPlanSeconds}+{MarginSeconds}={measuredTotal.ToString("0.0", CultureInfo.InvariantCulture)} <= {deadline.TotalSeconds}")
            .AppendLine($"sourceAndManifestPlanSeconds={SourceAndManifestPlanSeconds} (measured at the take level and recorded in the V-Bounce's record)")
            .AppendLine("processTreePeak=not measured (the delivered host reports none)")
            .ToString());
    }

    /// <summary>Writes the package and the fixture the known-bad control is printed from.</summary>
    /// <returns>The fixture's identifier.</returns>
    private string PrepareAuditRender()
    {
        var files = AtBoundFixtures.Package();
        var derived = AtBoundSamples.Derive(files, Template, Version);
        Assert.True(derived.Result.Applies);

        var packageFolder = Path.Combine(suite.PackagesDirectory, Package);
        if (!Directory.Exists(packageFolder))
        {
            WritePackage(suite.PackagesDirectory, Package, files);
        }

        var fixturePath = Path.Combine(suite.FixturesDirectory, AuditFixture + ".json");
        if (!File.Exists(fixturePath))
        {
            File.WriteAllBytes(fixturePath, AuditSnapshot(derived.Result.Document!));
        }

        return AuditFixture;
    }

    /// <summary>
    /// The audit render's snapshot: an ordinary record first and the record derived at the
    /// bound second, so the control's one-character loss lands on the at-bound row.
    /// </summary>
    private static byte[] AuditSnapshot(byte[] atBoundDocument)
    {
        var document = JsonNode.Parse(Encoding.UTF8.GetString(atBoundDocument))!.AsObject();
        var bound = document["items"]!.AsArray()[0]!.AsObject().DeepClone().AsObject();
        bound["sequence"] = 2;
        var ordinary = new JsonObject
        {
            ["sequence"] = 1,
            ["description"] = "Consulting",
            ["amount"] = 100,
        };

        var snapshot = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["header"] = document["header"]!.DeepClone(),
            ["items"] = new JsonArray(ordinary, bound),
        };
        return Encoding.UTF8.GetBytes(snapshot.ToJsonString(Writing));
    }

    /// <summary>The document's record carries exactly the row bound in its measured field.</summary>
    private static void AssertBoundValue(byte[] atBoundDocument)
    {
        var document = JsonNode.Parse(Encoding.UTF8.GetString(atBoundDocument))!.AsObject();
        var records = document["items"]!.AsArray();
        var record = Assert.Single(records)!.AsObject();
        var value = record["description"]!.GetValue<string>();
        Assert.Equal(PdfRowBound.Bytes, Encoding.UTF8.GetByteCount(value));
        Assert.Equal(PdfRowBound.Bytes, value.Length);
    }

    private static string WritePackage(string parent, string name, IReadOnlyDictionary<string, byte[]> files)
    {
        var target = Path.Combine(parent, name);
        Directory.CreateDirectory(target);
        foreach (var (path, bytes) in files)
        {
            var full = Path.Combine(target, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        return target;
    }

    private static void WriteEvidence(string fileName, string text)
    {
        if (RunEnvironment.RunDirectory is null)
        {
            return;
        }

        RunEnvironment.WriteEvidence(fileName, Encoding.UTF8.GetBytes(text));
    }
}
