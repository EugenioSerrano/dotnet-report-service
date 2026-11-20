using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using ReportService.TemplateChecks.Sample;
using ReportService.TemplateSource.Checks;
using ReportService.Testing.TemplateChecks;

namespace ReportService.TemplateChecks.Tests.Sample;

/// <summary>
/// The take's sample-render check over the in-memory renderer: one case per row of the
/// refusal table - the file, the rule and the exact sentence an author reads - the
/// over-bound read that refuses before anything is composed, the two renders (the
/// sample and the empty sample), and the render that could not run, which raises and
/// never refuses. No engine, no browser and no PDF reader is reached.
/// </summary>
public sealed class SampleRenderCheckTests
{
    [Fact]
    public async Task AValidSampleIsRenderedTwiceAndPasses()
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson());
        var renderer = new InMemorySampleRenderer().AlwaysRendered();

        var result = await new SampleRenderCheck(renderer).CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.True(result.Passed, result.Reason);
        Assert.Equal(2, renderer.Requests.Count);
        Assert.False(renderer.Requests[0].EmptySample);
        Assert.True(renderer.Requests[1].EmptySample);
        foreach (var request in renderer.Requests)
        {
            Assert.Equal(candidate.TemplateId, request.TemplateId);
            Assert.Equal(candidate.Version, request.Version);
            Assert.Same(candidate.Manifest, request.Manifest);
            Assert.Equal(candidate.SourceDescriptorSha256, request.SourceDescriptorSha256);
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(package.Directory, "sample.json")),
                request.SampleJson.ToArray());
        }
    }

    [Fact]
    public async Task AnEngineErrorNamesThePartInTheAuthorsGrammar()
    {
        var result = await Refused(new SampleFailure(SampleFailureKind.EngineError, "html/row.html.sbn"), "scriban");

        Assert.Equal(
            "html/row.html.sbn: the part could not be rendered by scriban. Correct the template so that it renders.",
            result.Reason);
        Assert.Equal(TemplateRefusal.Rules.SampleEngineError, RuleOf(SampleFailureKind.EngineError));
    }

    [Theory]
    [InlineData("scriban", "engine-loop")]
    [InlineData("liquid", "engine-steps")]
    public async Task AnEngineLimitNamesTheEngineItsLimitAndThePart(string engine, string limit)
    {
        var result = await Refused(
            new SampleFailure(SampleFailureKind.EngineLimit, "html/row.html" + Packages.Extension(engine), Limit: limit),
            engine);

        Assert.StartsWith("html/row.html" + Packages.Extension(engine) + ": ", result.Reason, StringComparison.Ordinal);
        Assert.Contains(engine + "'s " + limit + " was reached while rendering this part.", result.Reason, StringComparison.Ordinal);
        Assert.EndsWith("Reduce the work the template does per record.", result.Reason, StringComparison.Ordinal);
        Assert.Equal(TemplateRefusal.Rules.SampleEngineLimit, RuleOf(SampleFailureKind.EngineLimit));
    }

    [Fact]
    public async Task TheOutputGuardIsItsOwnRefusal()
    {
        var result = await Refused(new SampleFailure(SampleFailureKind.GuardOutput, "html/row.html.sbn"));

        Assert.Contains("more output than a template may produce (16 Mi characters per call, 32 MiB per block)", result.Reason, StringComparison.Ordinal);
        Assert.EndsWith("Print less per record or per block.", result.Reason, StringComparison.Ordinal);
        Assert.Equal(TemplateRefusal.Rules.SampleOutputLimit, RuleOf(SampleFailureKind.GuardOutput));
    }

    [Fact]
    public async Task ASampleOverTheBoundIsRefusedBeforeAnythingIsRendered()
    {
        using var package = Packages.Package();
        File.WriteAllText(Path.Combine(package.Directory, "sample.json"), OverBoundSample());
        var candidate = package.Candidate(Packages.ManifestJson());
        var renderer = new InMemorySampleRenderer().AlwaysRendered();

        var result = await new SampleRenderCheck(renderer).CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Empty(renderer.Requests);
        Assert.Equal(
            "sample.json: the sample holds 2001 records and 18009 bytes of record data, where one block holds at most 2000 records and 1 MiB. Reduce the sample to a single block's worth of records.",
            result.Reason);
        Assert.Equal(TemplateRefusal.Rules.SampleOverBound, RuleOf(SampleFailureKind.OverBound));
    }

    [Fact]
    public async Task ARowThatPrintsNoTextNamesTheRowAndTheOrdinal()
    {
        var result = await Refused(new SampleFailure(
            SampleFailureKind.Verification,
            "html/row.html.sbn",
            RecordOrdinal: 2,
            Reason: "row-without-text"));

        Assert.Equal(
            "html/row.html.sbn: row 2 of the sample prints no text. Correct the row part so that every row prints the values it carries.",
            result.Reason);
        Assert.Equal(TemplateRefusal.Rules.SampleNotPrinted, RuleOf(SampleFailureKind.Verification));
    }

    [Fact]
    public async Task AnAnchorMismatchNamesTheManifestAndTheFieldPath()
    {
        var result = await Refused(new SampleFailure(
            SampleFailureKind.Verification,
            "html/row.html.sbn",
            AnchorPath: "$.customer.name",
            Reason: "anchor-mismatch"));

        Assert.Equal(
            "template.yaml: $.customer.name is declared as an anchor but the template does not print it as it is. Print the field as it is, or remove it from the anchor list.",
            result.Reason);
    }

    [Fact]
    public async Task AnUndeclaredAnchorListNamesTheManifest()
    {
        var result = await Refused(new SampleFailure(
            SampleFailureKind.Verification,
            Reason: "anchors-undeclared"));

        Assert.Equal(
            "template.yaml: the package declares no field it prints as it is, so the page cannot be verified. Print the field as it is, or remove it from the anchor list.",
            result.Reason);
    }

    [Fact]
    public async Task AnUnreadableSampleIsRefusedInTheFormatGrammar()
    {
        var result = await Refused(new SampleFailure(
            SampleFailureKind.Unreadable,
            "sample.json",
            Reason: "header-not-first"));

        Assert.Equal(
            "sample.json: the sample does not follow the data format (header-not-first). Correct the sample.",
            result.Reason);
        Assert.Equal(TemplateRefusal.Rules.SampleFormat, RuleOf(SampleFailureKind.Unreadable));
    }

    [Fact]
    public async Task APageWithNoContentNamesThePart()
    {
        var result = await Refused(new SampleFailure(
            SampleFailureKind.Verification,
            "html/row.html.sbn",
            Reason: "blank-page"));

        Assert.Equal(
            "html/row.html.sbn: the sample produced a page with no content. Correct the page settings or the parts so that every page carries its content.",
            result.Reason);
    }

    [Theory]
    [InlineData("text-dropped")]
    [InlineData("row-lost")]
    [InlineData("ambiguous-alignment")]
    public async Task AnotherVerificationReasonKeepsItsOwnWord(string reason)
    {
        var result = await Refused(new SampleFailure(
            SampleFailureKind.Verification,
            "html/row.html.sbn",
            Reason: reason));

        Assert.Equal(
            "html/row.html.sbn: the printed page does not carry what the parts composed (" + reason + "). Correct the part the page prints from.",
            result.Reason);
    }

    [Fact]
    public async Task AnUnusablePageDeclarationNamesTheManifestAndTheFix()
    {
        var result = await Refused(new SampleFailure(
            SampleFailureKind.Verification,
            "template.yaml",
            Reason: "page-settings-invalid"));

        Assert.Equal(
            "template.yaml: the page settings are missing or not usable. Declare outputs.pdf.paper, orientation and the four margins.",
            result.Reason);
    }

    [Fact]
    public async Task DeclaredPageSubtotalsAreRefusedInTheirOwnWords()
    {
        var result = await Refused(new SampleFailure(
            SampleFailureKind.Verification,
            "template.yaml",
            Reason: "page-accounting"));

        Assert.Equal(
            "template.yaml: the template declares page subtotals, which this service does not print. Print the totals in the template or turn them off.",
            result.Reason);
    }

    [Fact]
    public async Task AnEmptyReportThatPrintsNothingNamesTheDocumentPart()
    {
        var result = await Refused(new SampleFailure(
            SampleFailureKind.EmptySample,
            "html/document-start.html.sbn",
            Reason: "anchor-mismatch"));

        Assert.Equal(
            "html/document-start.html.sbn: the sample cannot print a report with no records (anchor-mismatch). Make the document parts render their header for zero records.",
            result.Reason);
        Assert.Equal(TemplateRefusal.Rules.SampleEmpty, RuleOf(SampleFailureKind.EmptySample));
    }

    [Theory]
    [InlineData(SampleUnavailableCause.RenderTimeout)]
    [InlineData(SampleUnavailableCause.RenderCrash)]
    public async Task ARenderThatCannotRunIsRaisedAndNeverRefused(SampleUnavailableCause cause)
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson());
        var renderer = new InMemorySampleRenderer().AlwaysUnavailable(cause);

        var raised = await Assert.ThrowsAsync<SampleRenderUnavailableException>(
            () => new SampleRenderCheck(renderer).CheckAsync(candidate, TestContext.Current.CancellationToken));

        Assert.Equal(cause, raised.Cause);
    }

    [Fact]
    public async Task AMissingSampleIsRefusedInItsOwnName()
    {
        using var package = Packages.Package();
        File.Delete(Path.Combine(package.Directory, "sample.json"));
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await new SampleRenderCheck(new InMemorySampleRenderer().AlwaysRendered())
            .CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.Equal("sample.json: the sample is missing. Add the sample the take renders.", result.Reason);
    }

    [Fact]
    public async Task NoValueOfTheSampleAppearsInAReason()
    {
        const string planted = "PLANTED-VALUE-9f31";
        using var package = Packages.Package();
        File.WriteAllText(
            Path.Combine(package.Directory, "sample.json"),
            """{ "contractVersion": "1.0", "header": { "title": "PLANTED-VALUE-9f31" }, "items": [ { "quantity": 1 } ] }""");
        var candidate = package.Candidate(Packages.ManifestJson());

        foreach (var failure in new[]
        {
            new SampleFailure(SampleFailureKind.EngineError, "html/row.html.sbn"),
            new SampleFailure(SampleFailureKind.Verification, "html/row.html.sbn", RecordOrdinal: 1, Reason: "text-dropped"),
            new SampleFailure(SampleFailureKind.EmptySample, "html/document-start.html.sbn", Reason: "no-page"),
        })
        {
            var result = await new SampleRenderCheck(new InMemorySampleRenderer().AlwaysFailed(failure))
                .CheckAsync(candidate, TestContext.Current.CancellationToken);

            Assert.False(result.Passed);
            Assert.DoesNotContain(planted, result.Reason, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(SampleFailureKind.EngineError, TemplateRefusal.Rules.SampleEngineError)]
    [InlineData(SampleFailureKind.EngineLimit, TemplateRefusal.Rules.SampleEngineLimit)]
    [InlineData(SampleFailureKind.GuardOutput, TemplateRefusal.Rules.SampleOutputLimit)]
    [InlineData(SampleFailureKind.OverBound, TemplateRefusal.Rules.SampleOverBound)]
    [InlineData(SampleFailureKind.Verification, TemplateRefusal.Rules.SampleNotPrinted)]
    [InlineData(SampleFailureKind.EmptySample, TemplateRefusal.Rules.SampleEmpty)]
    [InlineData(SampleFailureKind.Unreadable, TemplateRefusal.Rules.SampleFormat)]
    public void TheRuleLabelIsTheTablesOwn(SampleFailureKind kind, string rule) =>
        Assert.Equal(rule, RuleOf(kind));

    private static string RuleOf(SampleFailureKind kind) =>
        SampleRenderCheck.RuleOf(new SampleFailure(kind));

    private async Task<TemplateCheckResult> Refused(SampleFailure failure, string engine = "scriban")
    {
        using var package = Packages.Package(engine);
        var candidate = package.Candidate(Packages.ManifestJson(engine));
        var renderer = new InMemorySampleRenderer().AlwaysFailed(failure);

        var result = await new SampleRenderCheck(renderer).CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        return result;
    }

    /// <summary>A sample of 2 001 records of nine bytes each: over the record bound, under the byte bound.</summary>
    private static string OverBoundSample()
    {
        var records = string.Join(',', Enumerable.Range(0, 2_001).Select(_ => """{"p":"x"}"""));
        return new StringBuilder()
            .Append("""{ "contractVersion": "1.0", "header": { "title": "Invoice" }, "items": [ """)
            .Append(records)
            .Append(" ] }")
            .ToString();
    }

    [Theory]
    [InlineData(SampleFailureKind.EngineError, TemplateRefusal.Rules.SampleEngineError)]
    [InlineData(SampleFailureKind.EngineLimit, TemplateRefusal.Rules.SampleEngineLimit)]
    [InlineData(SampleFailureKind.GuardOutput, TemplateRefusal.Rules.SampleOutputLimit)]
    [InlineData(SampleFailureKind.OverBound, TemplateRefusal.Rules.SampleOverBound)]
    [InlineData(SampleFailureKind.EmptySample, TemplateRefusal.Rules.SampleEmpty)]
    [InlineData(SampleFailureKind.Unreadable, TemplateRefusal.Rules.SampleFormat)]
    [InlineData(SampleFailureKind.Verification, TemplateRefusal.Rules.SampleNotPrinted)]
    public async Task ARefusalIsLoggedWithItsCheckRuleFileAndCode(SampleFailureKind kind, string rule)
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson());
        var failure = new SampleFailure(
            kind,
            "html/row.html.sbn",
            Reason: kind == SampleFailureKind.Verification ? "text-dropped" : null);
        using var logs = new CapturedLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var check = new SampleRenderCheck(
            new InMemorySampleRenderer().AlwaysFailed(failure),
            factory.CreateLogger<SampleRenderCheck>());

        var result = await check.CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.StartsWith("ReportService.TemplateChecks.Sample", entry.Category, StringComparison.Ordinal);
        Assert.Equal("sample-render", entry.State["check"]);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, entry.State["code"]);
        Assert.Equal(rule, entry.State["rule"]);
        Assert.Equal("html/row.html.sbn", entry.State["file"]);
        Assert.Equal("scriban", entry.State["engine"]);
        Assert.Equal("refused", entry.State["outcome"]);
    }

    [Fact]
    public async Task AMissingSampleIsLoggedUnderItsOwnRule()
    {
        using var package = Packages.Package();
        File.Delete(Path.Combine(package.Directory, "sample.json"));
        var candidate = package.Candidate(Packages.ManifestJson());
        using var logs = new CapturedLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var check = new SampleRenderCheck(
            new InMemorySampleRenderer().AlwaysRendered(),
            factory.CreateLogger<SampleRenderCheck>());

        var result = await check.CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(TemplateRefusal.Rules.SampleMissing, entry.State["rule"]);
        Assert.Equal("sample.json", entry.State["file"]);
        Assert.Equal("refused", entry.State["outcome"]);
    }

    [Fact]
    public async Task AnAcceptedSampleIsLoggedWithItsFigures()
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson());
        using var logs = new CapturedLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var check = new SampleRenderCheck(
            new InMemorySampleRenderer().AlwaysRendered(),
            factory.CreateLogger<SampleRenderCheck>());

        var result = await check.CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        var entry = Assert.Single(logs.Entries);
        var facts = InMemorySampleRenderer.Facts;
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.StartsWith("ReportService.TemplateChecks.Sample", entry.Category, StringComparison.Ordinal);
        Assert.Equal("sample-render", entry.State["check"]);
        Assert.Equal("scriban", entry.State["engine"]);
        Assert.Equal("accepted", entry.State["outcome"]);
        Assert.Equal(facts.Pages, entry.State["pages"]);
        Assert.Equal(facts.HtmlBytes, entry.State["htmlBytes"]);
        Assert.Equal(facts.PdfBytes, entry.State["pdfBytes"]);
        Assert.Equal(facts.Words, entry.State["words"]);
        Assert.Equal(facts.ComposeMs, entry.State["composeMs"]);
        Assert.Equal(facts.PrintMs, entry.State["printMs"]);
        Assert.Equal(facts.VerifyMs, entry.State["verifyMs"]);
    }

    [Fact]
    public async Task ARefusalOpensTheTemplateSampleSpan()
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson());
        var failure = new SampleFailure(SampleFailureKind.Verification, "html/row.html.sbn", Reason: "text-dropped");
        var spans = new List<Activity>();
        using var listener = Listen(spans);
        var check = new SampleRenderCheck(new InMemorySampleRenderer().AlwaysFailed(failure));

        var result = await check.CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        var span = Assert.Single(spans);
        Assert.Equal("template.sample", span.DisplayName);
        Assert.Equal("scriban", span.GetTagItem("engine"));
        Assert.Equal("refused", span.GetTagItem("outcome"));
        Assert.Equal(TemplateRefusal.Rules.SampleNotPrinted, span.GetTagItem("rule"));
        Assert.Equal("html/row.html.sbn", span.GetTagItem("file"));
    }

    [Fact]
    public async Task AnAcceptedSampleOpensTheTemplateSampleSpanWithItsPages()
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson());
        var spans = new List<Activity>();
        using var listener = Listen(spans);
        var check = new SampleRenderCheck(new InMemorySampleRenderer().AlwaysRendered());

        var result = await check.CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        var span = Assert.Single(spans);
        Assert.Equal("scriban", span.GetTagItem("engine"));
        Assert.Equal("accepted", span.GetTagItem("outcome"));
        Assert.Equal(InMemorySampleRenderer.Facts.Pages, span.GetTagItem("pages"));
    }

    [Fact]
    public async Task NeitherTheLogNorTheSpanCarriesAValueOfTheSample()
    {
        const string planted = "PLANTED-VALUE-9f31";
        using var package = Packages.Package();
        File.WriteAllText(
            Path.Combine(package.Directory, "sample.json"),
            """{ "contractVersion": "1.0", "header": { "title": "PLANTED-VALUE-9f31" }, "items": [ { "quantity": 1 } ] }""");
        var candidate = package.Candidate(Packages.ManifestJson());
        var failure = new SampleFailure(SampleFailureKind.EngineError, "html/row.html.sbn");
        using var logs = new CapturedLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var spans = new List<Activity>();
        using var listener = Listen(spans);
        var check = new SampleRenderCheck(
            new InMemorySampleRenderer().AlwaysFailed(failure),
            factory.CreateLogger<SampleRenderCheck>());

        var result = await check.CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        var entry = Assert.Single(logs.Entries);
        foreach (var (_, value) in entry.State)
        {
            Assert.DoesNotContain(planted, value?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }

        var span = Assert.Single(spans);
        foreach (var tag in span.TagObjects)
        {
            Assert.DoesNotContain(planted, tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }
    }

    private static ActivityListener Listen(List<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, "ReportService", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (string.Equals(activity.DisplayName, SampleRenderCheck.SpanName, StringComparison.Ordinal))
                {
                    spans.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

/// <summary>One captured log record: the category the factory built the logger with, the level and its state.</summary>
/// <param name="Level">The record's level.</param>
/// <param name="Category">The category the logger was created with.</param>
/// <param name="State">The record's structured state.</param>
internal sealed record CapturedLog(LogLevel Level, string Category, IReadOnlyDictionary<string, object?> State);

/// <summary>The provider a case listens with: it keeps every record the factory writes.</summary>
internal sealed class CapturedLoggerProvider : ILoggerProvider
{
    private readonly List<CapturedLog> _entries = [];

    /// <summary>The records written through this provider, in order.</summary>
    public List<CapturedLog> Entries => _entries;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CapturedLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class CapturedLogger(CapturedLoggerProvider owner, string category) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    values[key] = value;
                }
            }

            owner._entries.Add(new CapturedLog(logLevel, category, values));
        }
    }
}
