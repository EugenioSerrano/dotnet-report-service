using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Rendering;
using ReportService.TemplateChecks;
using ReportService.TemplateChecks.Checks;
using ReportService.TemplateChecks.Sample;
using ReportService.TemplateChecks.Scanning;
using ReportService.TemplateSource.Checks;
using ReportService.Testing.TemplateChecks;

namespace ReportService.TemplateChecks.Tests;

/// <summary>The thirteen checks, their order, and the refusals an author reads.</summary>
public sealed class TemplateChecksTests
{
    [Fact]
    public void TheRegisteredChecksAreTheFixedOrder()
    {
        var services = new ServiceCollection()
            .AddSingleton<ISampleRenderer>(new InMemorySampleRenderer().AlwaysRendered())
            .AddTemplateVersionChecks()
            .BuildServiceProvider();
        var names = services.GetServices<ITemplateVersionCheck>().Select(check => check.Name).ToArray();
        Assert.Equal(TemplateChecksRegistration.Order, names);
        Assert.Equal(AtBoundSampleCheck.CheckName, names[^1]);
        Assert.Equal(SampleRenderCheck.CheckName, names[^2]);
    }

    [Fact]
    public void TheDocumentBaseIsTheRenderers()
    {
        Assert.Equal(TemplateDocumentBase.Url, PackageRoutePolicy.DocumentUrl);
    }

    [Theory]
    [InlineData("scriban", "7.5.1")]
    [InlineData("handlebars", "2.4.3")]
    [InlineData("liquid", "2.40.0")]
    public async Task AValidPackagePassesEveryCheck(string engine, string version)
    {
        using var package = Packages.Package(engine);
        var candidate = package.Candidate(Packages.ManifestJson(engine, engineVersion: version));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task ASyntaxErrorNamesTheFile()
    {
        using var package = Packages.Package(row: "<tr><td>{{ if }}</td></tr>");
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.Contains("html/row.html.sbn", result.Reason, StringComparison.Ordinal);
        Assert.Contains("syntax error", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACatastrophicPatternIsRefused()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": {
                "header": { "type": "object", "properties": { "title": { "type": "string", "pattern": "^(a+)+$" } } },
                "items": { "type": "array", "items": { "type": "object" } }
              }
            }
            """;
        using var package = TempPackage.Write(
            ("schema.json", schema),
            ("sample.json", Packages.Sample),
            ("html/document-start.html.sbn", "<html>"),
            ("html/row.html.sbn", "<tr></tr>"),
            ("html/document-end.html.sbn", "</html>"),
            ("html/styles.css", "body {}"));
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("schema.json", result.Reason, StringComparison.Ordinal);
        Assert.Contains("repeats", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASchemaOutsideTheProfileNamesTheRule()
    {
        const string schema = """
            { "$schema": "http://json-schema.org/draft-07/schema#", "type": "object" }
            """;
        using var package = TempPackage.Write(
            ("schema.json", schema),
            ("sample.json", Packages.Sample),
            ("html/document-start.html.sbn", "<html>"),
            ("html/row.html.sbn", "<tr></tr>"),
            ("html/document-end.html.sbn", "</html>"),
            ("html/styles.css", "body {}"));
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("dialect", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASampleViolationNamesItsPath()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": {
                "contractVersion": { "type": "string" },
                "header": { "type": "object", "properties": { "title": { "type": "string" } } },
                "items": { "type": "array", "items": { "type": "object", "properties": { "quantity": { "type": "integer", "maximum": 3 } } } }
              }
            }
            """;
        const string sample = """{ "contractVersion": "1.0", "header": { "title": "Invoice" }, "items": [ { "quantity": 9 } ] }""";
        using var package = TempPackage.Write(
            ("schema.json", schema),
            ("sample.json", sample),
            ("html/document-start.html.sbn", "<html>"),
            ("html/row.html.sbn", "<tr></tr>"),
            ("html/document-end.html.sbn", "</html>"),
            ("html/styles.css", "body {}"));
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("$.items[0].quantity", result.Reason, StringComparison.Ordinal);
        Assert.Contains("maximum", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("aggregates")]
    [InlineData("verifiedTotals")]
    [InlineData("groupOrder")]
    public async Task ADeclarationThisServiceDoesNotDeliverIsRefused(string feature)
    {
        using var package = Packages.Package();
        var manifest = feature switch
        {
            "aggregates" => Packages.ManifestJson(aggregates: "total"),
            "verifiedTotals" => Packages.ManifestJson(verifiedTotals: "true"),
            _ => Packages.ManifestJson(groupOrder: "ascending"),
        };
        var candidate = package.Candidate(manifest);

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.UnsupportedCapability, result.Code);
        Assert.Contains(feature, result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A5", "portrait")]
    [InlineData("A4", "sideways")]
    public async Task AnUnusablePageSettingNamesTheField(string paper, string orientation)
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson(paper: paper, orientation: orientation));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("outputs.pdf.", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMarginMemberThatIsMissingIsRefusedWithItsField()
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson(
            margin: """{ "top": 15, "right": 12, "bottom": 18 }"""));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("outputs.pdf.marginMm.left", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAdmittedPaperCaseInsensitivelyPasses()
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson(paper: "a4"));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AnAnchorTheSchemaDoesNotDeclareIsRefused()
    {
        using var package = Packages.Package();
        var manifest = Packages.ManifestJson().Replace("$.title", "$.missing", StringComparison.Ordinal);
        var candidate = package.Candidate(manifest);

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("$.missing", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnescapedScribanOutputIsRefused()
    {
        using var package = Packages.Package(row: "<tr><td>{{ item.quantity }}</td></tr>");
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("html.escape", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARawHandlebarsOutputIsRefused()
    {
        using var package = Packages.Package("handlebars", "<tr><td>{{{ item.quantity }}}</td></tr>");
        var candidate = package.Candidate(Packages.ManifestJson("handlebars", engineVersion: "2.4.3"));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("raw output", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALiquidRawFilterIsRefused()
    {
        using var package = Packages.Package("liquid", "<tr><td>{{ item.quantity | raw }}</td></tr>");
        var candidate = package.Candidate(Packages.ManifestJson("liquid", engineVersion: "2.40.0"));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("raw", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADataOutputInAnAttributeIsRefused()
    {
        using var package = Packages.Package(row: """<a href="{{ item.quantity | html.escape }}">x</a>""");
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("href", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForbiddenFunctionIsRefused()
    {
        using var package = Packages.Package(row: """<tr><td>{{ object.eval("x") }}</td></tr>""");
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("object.eval", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACustomIncludeThatIsNotAPartIsRefused()
    {
        using var package = Packages.Package(
            row: """<tr><td>{{ include "partials/head.sbn" }}</td></tr>""");
        var head = Path.Combine(package.Directory, "partials", "head.sbn");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(head)!);
        await File.WriteAllTextAsync(head, "<b></b>", TestContext.Current.CancellationToken);
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("is not a part this render loads", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingAssetIsRefused()
    {
        using var package = Packages.Package(row: """<tr><td><img src="logo.png"></td></tr>""");
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("logo.png", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEngineVersionOfAnotherMajorIsRefused()
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson(engineVersion: "6.0.0"));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.UnsupportedCapability, result.Code);
        Assert.Contains("6.0.0", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEarlierFaultIsTheOneReported()
    {
        using var package = Packages.Package(row: "<tr><td>{{ if }}</td></tr>");
        var candidate = package.Candidate(Packages.ManifestJson(aggregates: "total"));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("aggregates", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalNamesTheFileAndTheFixAndFitsTheBudget()
    {
        var result = TemplateRefusal.Refuse(
            TemplateCheckCodes.TemplateInvalid,
            "html/row.html.sbn",
            "the output at line 1 writes a data value without escaping.",
            new string('x', 2_000));

        Assert.StartsWith("html/row.html.sbn:", result.Reason, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(result.Reason!) <= TemplateRefusal.MaximumBytes);
        Assert.EndsWith("…", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEchoedNameIsBounded()
    {
        Assert.Equal("row.sbn", TemplateRefusal.Echo("row.sbn"));
        Assert.Equal("(name not shown)", TemplateRefusal.Echo(new string('a', 129)));
        Assert.Equal("(name not shown)", TemplateRefusal.Echo("row\n.sbn"));
    }

    [Fact]
    public void TheSchemeIsShownOnlyWhenItIsOne()
    {
        Assert.Equal("https:", TemplateRefusal.Scheme("https://example.invalid/x.png"));
        Assert.Equal("scheme not shown", TemplateRefusal.Scheme("//host/x"));
        Assert.Equal("scheme not shown", TemplateRefusal.Scheme("relative/x"));
    }

    [Fact]
    public async Task TheBudgetStopsTheChecks()
    {
        var clock = new ManualClock();
        var session = new TemplateCheckSession(clock);
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson());
        _ = session.View(candidate);
        clock.Advance(TimeSpan.FromSeconds(31));
        _ = session.BudgetExceeded(candidate);

        var result = await new ManifestAndPathsCheck(session).CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Contains("30 s", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("^(a+)+$", true)]
    [InlineData("(\\w*)*", true)]
    [InlineData("(\\d+,){2,5}", true)]
    [InlineData("((ab)+c)*", true)]
    [InlineData("^[a-z]+$", false)]
    [InlineData("^(ab)+$", false)]
    [InlineData("^(\\d+,)?\\d+$", false)]
    [InlineData("^\\d{4}-\\d{2}$", false)]
    [InlineData("^(a{1,3}){2}$", false)]
    [InlineData("(a|a)*", false)]
    public void ThePatternRuleRefusesWhatItCanSee(string pattern, bool refused)
    {
        Assert.Equal(refused, SchemaPatternRule.IsCatastrophic(pattern));
    }

    [Theory]
    [InlineData("""<tr><td>{{ item.quantity | html.escape }}</td></tr>{{ if item.sequence }}x{{ end }}""")]
    [InlineData("""<tr>{{ for line in item.lines }}<td>{{ line.name | html.escape }}</td>{{ end }}</tr>""")]
    [InlineData("""<tr><td>{{ if item.a }}{{ item.a | html.escape }}{{ else if item.b }}{{ item.b | html.escape }}{{ else }}{{ item.quantity | html.escape }}{{ end }}</td></tr>""")]
    [InlineData("""<tr>{{ if item.a }}{{ for line in item.lines }}{{ for part in line.parts }}<td>{{ part.name | html.escape }}</td>{{ end }}{{ end }}{{ end }}</tr>""")]
    public async Task AScribanTemplateWithControlFlowOverDataAndEscapedOutputsIsTaken(string row)
    {
        using var package = Packages.Package(row: row);
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.True(result.Passed, result.Reason);
    }

    [Theory]
    [InlineData("""<tr>{{ for line in item.lines }}<td>{{ line.name }}</td>{{ end }}</tr>""")]
    [InlineData("""<tr>{{ for line in item.lines }}<td>{{ if line.a }}x{{ else if line.b }}{{ line.name }}{{ end }}</td>{{ end }}</tr>""")]
    [InlineData("""<tr>{{ for line in item.lines }}{{ for part in line.parts }}<td>{{ part.name }}</td>{{ end }}{{ end }}</tr>""")]
    [InlineData("""<tr>{{ for line in item.lines }}<td>{{ if line.a }}{{ line.name }}{{ end }}</td>{{ end }}</tr>""")]
    [InlineData("""<tr><td>{{ if item.a }}{{ item.a }}{{ end }}</td></tr>""")]
    public async Task AnUnescapedScribanDataOutputInsideControlFlowIsRefused(string row)
    {
        using var package = Packages.Package(row: row);
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("writes a data value without escaping", result.Reason, StringComparison.Ordinal);
    }
}
