using System.Text;
using ReportService.TemplateChecks.Checks;
using ReportService.TemplateChecks.Scanning;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.Tests;

/// <summary>The source scans, the HTML and CSS readers, the package view and the checks' edges.</summary>
public sealed class ScanningAndChecksTests
{
    [Fact]
    public void ScribanScanFindsOutputsAssignmentsAndIncludes()
    {
        var scan = TemplateSourceScan.Scan(
            """<p>{{ item.amount }}</p><p>{{ item.amount | html.escape }}</p>{{ x = item.name }}{{ x }}<p>{{ include "html/row.html.sbn" }}</p><p>{{ include name }}</p>""",
            TemplateDialect.Scriban);

        Assert.Equal(3, scan.Outputs.Count);
        Assert.True(scan.Outputs[0].IsData);
        Assert.False(scan.Outputs[0].IsEscaped);
        Assert.True(scan.Outputs[1].IsEscaped);
        Assert.True(scan.Outputs[2].IsData);
        Assert.False(scan.Outputs[2].IsEscaped);
        Assert.Equal(2, scan.Includes.Count);
        Assert.True(scan.Includes[0].IsLiteral);
        Assert.False(scan.Includes[1].IsLiteral);
    }

    [Fact]
    public void HandlebarsScanFindsRawOutputsPartialsAndComments()
    {
        var scan = TemplateSourceScan.Scan(
            """{{! a comment }}{{#if x}}{{/if}}{{{ item.amount }}}{{& item.amount}}{{> partials/head}}{{> (which)}}<p>{{ item.amount }}</p>""",
            TemplateDialect.Handlebars);

        Assert.Equal(3, scan.Outputs.Count);
        Assert.True(scan.Outputs[0].IsRaw);
        Assert.True(scan.Outputs[1].IsRaw);
        Assert.False(scan.Outputs[2].IsRaw);
        Assert.Equal(2, scan.Includes.Count);
        Assert.False(scan.Includes[1].IsLiteral);
    }

    [Fact]
    public void LiquidScanFindsRawBlocksFiltersRangesAndTags()
    {
        var scan = TemplateSourceScan.Scan(
            """{% raw %}{{ x }}{% endraw %}{{ item.amount | raw }}{% assign y = item.amount %}{{ y }}{% include "partials/head" %}{% render 'a/b' %}{% for i in (1..n) %}{{ i }}{% endfor %}{% increment counter %}""",
            TemplateDialect.Liquid);

        Assert.Contains(scan.Outputs, output => output.IsRaw && !output.IsData);
        Assert.Contains(scan.Outputs, output => output.IsData && !output.IsEscaped);
        Assert.Contains(scan.Outputs, output => output.IsData && output.IsEscaped);
        Assert.Equal(2, scan.Includes.Count);
        Assert.Contains(scan.Calls, call => call.Name == "increment");
        var range = Assert.Single(scan.Ranges);
        Assert.True(range.FromIsInteger);
        Assert.False(range.ToIsInteger);
        Assert.Null(range.Count);
    }

    [Fact]
    public void TheHtmlContextIsReadWhereTheOutputSits()
    {
        const string text = """<p>{{ x }}</p><script>{{ x }}</script><style>{{ x }}</style><a href="{{ x }}">y</a><td class={{ x }}>z</td>""";
        var scan = TemplateSourceScan.Scan(text, TemplateDialect.Scriban);

        var contexts = scan.Outputs.Select(output => HtmlScanner.ContextAt(text, output.Offset, scan.Regions).Kind).ToArray();
        Assert.Equal(HtmlContextKind.Text, contexts[0]);
        Assert.Equal(HtmlContextKind.Script, contexts[1]);
        Assert.Equal(HtmlContextKind.Style, contexts[2]);
        Assert.Equal(HtmlContextKind.AttributeValueQuoted, contexts[3]);
        Assert.Equal(HtmlContextKind.AttributeValueUnquoted, contexts[4]);
    }

    [Theory]
    [InlineData("href", true)]
    [InlineData("srcset", true)]
    [InlineData("onclick", true)]
    [InlineData("style", true)]
    [InlineData("class", false)]
    [InlineData("title", false)]
    [InlineData(null, false)]
    public void TheDangerousAttributesAreTheContract(string? attribute, bool dangerous)
    {
        Assert.Equal(dangerous, HtmlScanner.IsDangerousAttribute(attribute));
    }

    [Fact]
    public void TheAssetsAreTheReferencesTheRenderFetches()
    {
        const string text = """<link rel="stylesheet" href="html/styles.css"><img src="logo.png" srcset="a.png 1x, b.png 2x"><a href="page.html">x</a><svg><image href="i.svg"/></svg><p style="background: url(bg.png)"></p>""";

        var assets = HtmlScanner.Assets(text, []);

        Assert.Contains(assets, asset => asset.Url == "html/styles.css");
        Assert.Contains(assets, asset => asset.Url == "logo.png" && asset.Image);
        Assert.Contains(assets, asset => asset.Url == "a.png");
        Assert.Contains(assets, asset => asset.Url == "b.png");
        Assert.Contains(assets, asset => asset.Url == "i.svg");
        Assert.Contains(assets, asset => asset.Url == "bg.png");
        Assert.DoesNotContain(assets, asset => asset.Url == "page.html");
    }

    [Fact]
    public void ATemplateRegionIsNotAnAssetReference()
    {
        const string text = """<img src="{{ image }}">""";

        var assets = HtmlScanner.Assets(text, [new TemplateRegion(10, 11)]);

        Assert.Empty(assets);
    }

    [Fact]
    public void TheStylesheetReferencesAreReadWithTheirQuotes()
    {
        const string css = """a { background: url( "bg.png" ); } @import url(extra.css); @import "more.css";""";

        var urls = CssScanner.Urls(css);

        Assert.Equal(["bg.png", "extra.css", "more.css"], urls);
    }

    [Fact]
    public void ThePatternRuleWalksPatternPropertiesAndAnyWhereTheySit()
    {
        const string schema = """
            { "properties": { "a": { "patternProperties": { "^(a+)+$": { "type": "string" } } } } }
            """;
        var finding = SchemaPatternRule.Find(Encoding.UTF8.GetBytes(schema));

        Assert.NotNull(finding);
        Assert.Contains("^(a+)+$", finding.Pattern, StringComparison.Ordinal);
        Assert.Null(SchemaPatternRule.Find(Encoding.UTF8.GetBytes("{ not json")));
    }

    [Fact]
    public void ThePackageViewDecodesStrictlyAndSkipsTheByteOrderMark()
    {
        using var package = TempPackage.Write(("a.sbn", "abc"), ("b.sbn", "x"));
        var bytes = Encoding.UTF8.GetBytes("ok");
        File.WriteAllBytes(Path.Combine(package.Directory, "bom.sbn"), [0xEF, 0xBB, 0xBF, .. bytes]);
        File.WriteAllBytes(Path.Combine(package.Directory, "bad.sbn"), [0xFF, 0xFE]);
        var candidate = package.Candidate(Packages.ManifestJson());
        var view = new PackageView(candidate);

        Assert.Equal("ok", view.ReadText("bom.sbn"));
        Assert.Throws<DecoderFallbackException>(() => view.ReadText("bad.sbn"));
        Assert.False(view.Exists("missing.sbn"));
    }

    [Fact]
    public void TheSessionReplacesTheContractWhenTheCandidateChanges()
    {
        using var package = Packages.Package();
        using var session = new TemplateCheckSession(TimeProvider.System);
        var first = package.Candidate(Packages.ManifestJson());
        var second = package.Candidate(Packages.ManifestJson(), version: "1.2.0");
        var view = session.View(first);

        Assert.Same(view, session.View(first));
        Assert.NotSame(view, session.View(second));
    }

    [Fact]
    public async Task AManifestWithoutProfileOrPartsIsRefused()
    {
        using var package = Packages.Package();
        var withoutProfile = package.Candidate(
            Packages.ManifestJson().Replace("\"profile\":\"header-items-v1\",", string.Empty, StringComparison.Ordinal));
        var result = await Packages.RunAsync(Packages.Checks(), withoutProfile);
        Assert.False(result.Passed);
        Assert.Contains("dataContract.profile", result.Reason, StringComparison.Ordinal);

        var withoutParts = package.Candidate(
            Packages.ManifestJson().Replace("\"row\":\"html/row.html.sbn\",", string.Empty, StringComparison.Ordinal));
        result = await Packages.RunAsync(Packages.Checks(), withoutParts);
        Assert.False(result.Passed);
        Assert.Contains("documentStart, row and documentEnd", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingSchemaFileIsRefusedByName()
    {
        using var package = TempPackage.Write(
            ("sample.json", Packages.Sample),
            ("html/document-start.html.sbn", "<html>"),
            ("html/row.html.sbn", "<tr></tr>"),
            ("html/document-end.html.sbn", "</html>"),
            ("html/styles.css", "body {}"));
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("schema.json", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingSampleIsRefusedByName()
    {
        using var package = TempPackage.Write(
            ("schema.json", Packages.Schema),
            ("html/document-start.html.sbn", "<html>"),
            ("html/row.html.sbn", "<tr></tr>"),
            ("html/document-end.html.sbn", "</html>"),
            ("html/styles.css", "body {}"));
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("sample.json", result.Reason, StringComparison.Ordinal);
        Assert.Contains("missing", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASampleOfAnotherShapeIsRefusedByTheReader()
    {
        using var package = TempPackage.Write(
            ("schema.json", Packages.Schema),
            ("sample.json", """{ "items": [ { "quantity": 1 } ], "header": { "title": "x" } }"""),
            ("html/document-start.html.sbn", "<html>"),
            ("html/row.html.sbn", "<tr></tr>"),
            ("html/document-end.html.sbn", "</html>"),
            ("html/styles.css", "body {}"));
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("sample.json", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeclaredStyleThePackageDoesNotHoldIsRefused()
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(
            Packages.ManifestJson().Replace("\"html/styles.css\"", "\"html/missing.css\"", StringComparison.Ordinal));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("html/missing.css", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALiquidRangeWithAVariableBoundIsRefused()
    {
        using var package = Packages.Package("liquid", "{% for i in (1..n) %}{{ i }}{% endfor %}");
        var candidate = package.Candidate(Packages.ManifestJson("liquid", engineVersion: "2.40.0"));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("range", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALiquidRangeOverTenThousandValuesIsRefused()
    {
        using var package = Packages.Package("liquid", "{% for i in (1..20000) %}{{ i }}{% endfor %}");
        var candidate = package.Candidate(Packages.ManifestJson("liquid", engineVersion: "2.40.0"));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("10 000", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHandlebarsPartialNamingADeclaredPartIsRefused()
    {
        using var package = Packages.Package("handlebars", "<tr><td>{{> html/row.html}}</td></tr>");
        var candidate = package.Candidate(Packages.ManifestJson("handlebars", engineVersion: "2.4.3"));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("declared part", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIncludeCycleIsRefused()
    {
        using var package = TempPackage.Write(
            ("schema.json", Packages.Schema),
            ("sample.json", Packages.Sample),
            ("html/document-start.html.sbn", "<html>"),
            ("html/row.html.sbn", """<tr><td>{{ include "html/document-end.html.sbn" }}</td></tr>"""),
            ("html/document-end.html.sbn", """{{ include "html/row.html.sbn" }}"""),
            ("html/styles.css", "body {}"));
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("includes itself", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAbsoluteAssetIsRefusedByItsSchemeOnly()
    {
        using var package = Packages.Package(row: """<tr><td><img src="https://example.invalid/x.png"></td></tr>""");
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("https:", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADataUrlOutsideAnImageIsRefused()
    {
        using var package = Packages.Package(row: """<tr><td><link href="data:text/css,x"><img src="data:image/png;base64,AA"></td></tr>""");
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("data:", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForbiddenHandlebarsHelperIsRefused()
    {
        using var package = Packages.Package("handlebars", """<tr><td>{{log "x"}}</td></tr>""");
        var candidate = package.Candidate(Packages.ManifestJson("handlebars", engineVersion: "2.4.3"));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("log", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForbiddenLiquidTagIsRefused()
    {
        using var package = Packages.Package("liquid", "{% increment counter %}<tr><td>{{ item.quantity }}</td></tr>");
        var candidate = package.Candidate(Packages.ManifestJson("liquid", engineVersion: "2.40.0"));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Contains("increment", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionWithoutAPdfOutputPassesThePageAndAnchorChecks()
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson(withPdf: false));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AnInvalidEngineVersionIsRefused()
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson(engineVersion: "seven"));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.UnsupportedCapability, result.Code);
    }

    [Fact]
    public async Task AnAnchorDeclaredThroughAllOfPasses()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": {
                "header": { "type": "object", "properties": { "title": { "type": "string" } } },
                "items": { "type": "array", "items": { "allOf": [ { "type": "object", "properties": { "quantity": { "type": "integer" } } } ] } }
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

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AnUntypedManifestCannotReachTheChecks()
    {
        // The take refuses a manifest its typed view cannot read before the checks run;
        // the candidate's own type therefore always carries a typed view.
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson());

        Assert.NotNull(candidate.ManifestJson);
        Assert.NotNull(candidate.Manifest);
        var result = await Packages.RunAsync(Packages.Checks(), candidate);
        Assert.True(result.Passed, result.Reason);
    }

    [Theory]
    [InlineData("", "scheme not shown")]
    [InlineData(":x", "scheme not shown")]
    [InlineData("http://x", "http:")]
    public void TheSchemeIsEchoedOnlyWhenItIsOne(string url, string expected)
    {
        Assert.Equal(expected, TemplateRefusal.Scheme(url));
    }

    [Theory]
    [InlineData("""{{ if item.quantity }}x{{ end }}""", 0)]
    [InlineData("""{{ for line in item.lines }}<td>{{ line.name | html.escape }}</td>{{ end }}""", 1)]
    [InlineData("""{{ if item.a }}{{ item.a | html.escape }}{{ else if item.b }}{{ item.b | html.escape }}{{ else }}{{ item.c | html.escape }}{{ end }}""", 3)]
    [InlineData("""{{ if item.a }}{{ for line in item.lines }}{{ for part in line.parts }}{{ part.name | html.escape }}{{ end }}{{ end }}{{ end }}""", 1)]
    [InlineData("""{{ for a in item.lines }}{{ for b in a.parts }}{{ for c in b.cells }}{{ c.value | html.escape }}{{ end }}{{ end }}{{ end }}""", 1)]
    public void ScribanControlFlowTagsAreNotOutputs(string template, int escapedOutputs)
    {
        var scan = TemplateSourceScan.Scan(template, TemplateDialect.Scriban);

        Assert.Equal(escapedOutputs, scan.Outputs.Count);
        Assert.All(scan.Outputs, output => Assert.True(output.IsEscaped, output.Expression));
    }

    [Theory]
    [InlineData("""{{ for line in item.lines }}<td>{{ line.name }}</td>{{ end }}""", "line.name")]
    [InlineData("""{{ for line in item.lines }}{{ if line.a }}x{{ else if line.b }}{{ line.name }}{{ end }}{{ end }}""", "line.name")]
    [InlineData("""{{ for line in item.lines }}{{ if line.a }}{{ line.name }}{{ end }}{{ end }}""", "line.name")]
    [InlineData("""{{ for line in item.lines }}{{ for part in line.parts }}{{ part.name }}{{ end }}{{ end }}""", "part.name")]
    [InlineData("""{{ for a in item.lines }}{{ for b in a.parts }}{{ for c in b.cells }}{{ c.value }}{{ end }}{{ end }}{{ end }}""", "c.value")]
    [InlineData("""{{ if item.a }}{{ item.a }}{{ end }}""", "item.a")]
    [InlineData("""{{ for line in item.lines limit:2 }}{{ line }}{{ end }}""", "line")]
    [InlineData("""{{ for line in item.lines offset:1 }}{{ line }}{{ end }}""", "line")]
    [InlineData("""{{ for line in item.lines reversed }}{{ line }}{{ end }}""", "line")]
    [InlineData("""{{ for line in item.lines | array.limit 2 }}{{ line }}{{ end }}""", "line")]
    public void ScribanUnescapedDataInsideControlFlowIsStillAnUnescapedDataOutput(string template, string output)
    {
        var scan = TemplateSourceScan.Scan(template, TemplateDialect.Scriban);

        var found = Assert.Single(scan.Outputs, candidate => candidate.Expression == output);
        Assert.True(found.IsData);
        Assert.False(found.IsEscaped);
    }

    [Theory]
    [InlineData("""{{ for.index }}""")]
    [InlineData("""{{ endx }}""")]
    [InlineData("""{{ iffy }}""")]
    [InlineData("""{{ elsewhere }}""")]
    public void ScribanOutputsNamedLikeStatementWordsStayOutputs(string template)
    {
        var scan = TemplateSourceScan.Scan(template, TemplateDialect.Scriban);

        Assert.Single(scan.Outputs);
    }

    [Theory]
    [InlineData("""{{ iffy item.quantity }}""")]
    [InlineData("""{{ endx item.quantity }}""")]
    [InlineData("""{{ for item.lines }}""")]
    [InlineData("""{{ if item.quantity; item.quantity; end }}""")]
    [InlineData("{{ if item.quantity\nitem.quantity\nend }}")]
    public void ScribanTagsThatAreNotASingleControlFlowStatementKeepTheOutputPath(string template)
    {
        var scan = TemplateSourceScan.Scan(template, TemplateDialect.Scriban);

        var output = Assert.Single(scan.Outputs);
        Assert.True(output.IsData);
        Assert.False(output.IsEscaped);
    }

    [Fact]
    public void AScribanLoopOverNonDataDoesNotMakeItsVariableData()
    {
        var scan = TemplateSourceScan.Scan("""{{ for n in [1, 2] }}{{ n }}{{ end }}""", TemplateDialect.Scriban);

        var output = Assert.Single(scan.Outputs);
        Assert.False(output.IsData);
    }

    [Fact]
    public void AForbiddenCallInAScribanConditionIsStillACall()
    {
        var scan = TemplateSourceScan.Scan("""{{ if date.now }}x{{ end }}""", TemplateDialect.Scriban);

        Assert.NotEmpty(scan.Calls);
    }
}
