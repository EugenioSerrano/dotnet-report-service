namespace ReportService.Templates.Tests;

/// <summary>
/// The Liquid adapter: the product's escaping policy on every data output, the number text
/// of the context, includes confined to the package's declared parts, the presentation
/// filters bound to the block's own settings, and a comparison taken by the engine's own
/// <c>if</c> as a real boolean.
/// </summary>
public sealed class FluidTemplateAdapterTests
{
    private const string Engine = FluidTemplateAdapter.EngineName;

    [Fact]
    public async Task EveryDataOutputIsEncodedWithTheProductPolicy()
    {
        var block = await EngineCases.Compose(
            Engine,
            [(TemplatePart.DocumentStart, "<p>{{ header.name }}</p>")],
            header: """{"name":"<a & \"b\" 'c' \u00e9\u00a0>"}""",
            records: []);

        Assert.Equal(
            "<p>&lt;a &amp; &quot;b&quot; &#39;c&#39; &#233;&#160;&gt;</p>",
            block.Text.Trim());
    }

    [Fact]
    public async Task TheNumbersOfTheContextPrintAsTheyWereGiven()
    {
        var block = await EngineCases.Compose(
            Engine,
            [(TemplatePart.Row, "<tr><td>{{ item.unitPrice }}</td><td>{{ item.quantity }}</td></tr>")],
            header: "{}",
            records: ["""{"unitPrice":1000.00,"quantity":3}"""]);

        Assert.Contains("<tr><td>1000.00</td><td>3</td></tr>", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void APackageOfAnotherEngineIsRefused()
    {
        var refusal = Assert.Throws<TemplatePackageException>(() =>
            EngineCases.AdapterOf(Engine).Prepare(
                EngineCases.Manifest(ScribanTemplateAdapter.EngineName, (TemplatePart.DocumentStart, "<p></p>")),
                EngineCases.Files(Engine, [(TemplatePart.DocumentStart, "<p></p>")])));

        Assert.Contains("scriban", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{% if %}")]
    [InlineData("{{ item.amount ")]
    public void APartThatCannotBeParsedIsRefusedWhenThePackageIsPrepared(string source)
    {
        var refusal = Assert.Throws<TemplatePackageException>(() =>
            EngineCases.Prepare(Engine, [(TemplatePart.DocumentStart, source)]));

        Assert.Contains("documentStart", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIncludeNamesADeclaredPartOfThePackage()
    {
        var block = await EngineCases.Compose(
            Engine,
            [
                (TemplatePart.DocumentStart, "<p>{% include 'documentEnd' %}</p>"),
                (TemplatePart.DocumentEnd, "the end"),
            ],
            header: "{}",
            records: []);

        Assert.Equal("<p>the end</p>the end", block.Text.Trim());
    }

    [Fact]
    public async Task AnIncludeOfSomethingThePackageDoesNotDeclareIsRefused()
    {
        await Assert.ThrowsAsync<TemplatePackageException>(() =>
            EngineCases.Compose(Engine, [(TemplatePart.DocumentStart, "{% include '../../etc/passwd' %}")], header: "{}", records: []));
    }

    [Fact]
    public async Task ThePresentationTablePresentsThroughTheFiltersWithTheBlocksCulture()
    {
        var block = await EngineCases.Compose(
            Engine,
            [(TemplatePart.Row, "<tr><td>{{ item.amount | format_number: \"N2\" }}</td><td>{{ item.date | format_date: \"dd/MM/yyyy\" }}</td></tr>")],
            header: "{}",
            records: ["""{"amount":1000.00,"date":"2026-10-30T02:30:00Z"}"""]);

        Assert.Contains("<tr><td>1.000,00</td><td>29/10/2026</td></tr>", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBlocksClockIsTheAcceptanceInstantInTheBlocksZone()
    {
        var block = await EngineCases.Compose(
            Engine,
            [(TemplatePart.DocumentStart, "{{ render.clock | format_date: \"dd/MM/yyyy HH:mm\" }}")],
            header: "{}",
            records: []);

        Assert.Equal("01/11/2026 23:30", block.Text.Trim());
    }

    [Fact]
    public async Task APresentationRefusalReachesTheCallerAsTheBlocksOwnRefusal()
    {
        // The engine may raise a filter's exception as its own: what the renderer reads is
        // still the block's typed refusal, never the generic failure of a part that could not
        // render, so the consumer gets DATA_INVALID with its reason.
        var refusal = await Assert.ThrowsAsync<PresentationRefusedException>(() => EngineCases.Compose(
            Engine,
            [(TemplatePart.Row, "<tr><td>{{ item.amount | format_number: \"N2\" }}</td></tr>")],
            header: "{}",
            records: ["""{"amount":123456789012345678901234567890123}"""]));

        Assert.Equal("DATA_INVALID", refusal.Failure.Code);
        Assert.Equal("number-out-of-range", refusal.Failure.Reason);
        Assert.Equal("format_number", refusal.Failure.Function);
    }

    [Fact]
    public async Task AComparisonIsARealBooleanTakenByTheEnginesOwnIf()
    {
        var block = await EngineCases.Compose(
            Engine,
            [(TemplatePart.Row, "{% assign positive = item.amount | num_gt: \"0\" %}{% if positive %}true{% else %}false{% endif %}")],
            header: "{}",
            records: ["""{"amount":1000.00}""", """{"amount":-0.50}""", """{"amount":0}"""]);

        Assert.Equal(["true", "false", "false"], block.Rows.Select(row => block.TextOf(row)));
    }

    [Fact]
    public async Task ASignPrintsItsTextAndIsNeverACondition()
    {
        var block = await EngineCases.Compose(
            Engine,
            [(TemplatePart.Row, "{{ item.amount | num_sign }}|{% assign sign = item.amount | num_sign %}{% if sign %}truthy{% else %}falsey{% endif %}")],
            header: "{}",
            records: ["""{"amount":-0.50}""", """{"amount":0}"""]);

        // "0" and "-1" are non-empty strings, which every engine reads as true: that is why
        // a sign used as a condition is always the comparison, never num_sign.
        Assert.Equal(["-1|truthy", "0|truthy"], block.Rows.Select(row => block.TextOf(row)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task NothingAPartAssignsReachesTheNextPart()
    {
        var block = await EngineCases.Compose(
            Engine,
            [(TemplatePart.DocumentStart, "{% assign shared = 5 %}"), (TemplatePart.Row, "<tr><td>{{ shared }}</td></tr>")],
            header: "{}",
            records: ["""{"sequence":1}"""]);

        Assert.Equal("<tr><td></td></tr>", block.TextOf(block.Rows.Single()));
    }

    [Fact]
    public void TheEngineReportsTheNameAndTheVersionItRuns()
    {
        var adapter = new FluidTemplateAdapter();

        Assert.Equal("liquid", adapter.Engine);
        Assert.Equal("2.40.0", adapter.Version);
        Assert.Equal(100_000, FluidTemplateAdapter.MaxSteps);
        Assert.Equal(100, FluidTemplateAdapter.MaxRecursion);
    }
}
