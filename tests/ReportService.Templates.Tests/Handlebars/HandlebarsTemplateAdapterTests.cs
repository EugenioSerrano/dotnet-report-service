namespace ReportService.Templates.Tests;

/// <summary>
/// The Handlebars adapter: the product's escaping policy on every data output, the number
/// text of the context, includes confined to the package's declared parts, the presentation
/// helpers bound to the block's own settings, and a comparison taken by the engine's own
/// <c>#if</c> as a real boolean.
/// </summary>
public sealed class HandlebarsTemplateAdapterTests
{
    private const string Engine = HandlebarsTemplateAdapter.EngineName;

    [Fact]
    public void EveryDataOutputIsEncodedWithTheProductPolicy()
    {
        var block = EngineCases.Compose(
            Engine,
            [(TemplatePart.DocumentStart, "<p>{{ header.name }}</p>")],
            header: """{"name":"<a & \"b\" 'c' \u00e9\u00a0>"}""",
            records: []);

        Assert.Equal(
            "<p>&lt;a &amp; &quot;b&quot; &#39;c&#39; &#233;&#160;&gt;</p>",
            block.Text.Trim());
    }

    [Fact]
    public void TheNumbersOfTheContextPrintAsTheyWereGiven()
    {
        var block = EngineCases.Compose(
            Engine,
            [(TemplatePart.Row, "<tr><td>{{ item.unitPrice }}</td><td>{{ item.quantity }}</td></tr>")],
            header: "{}",
            records: ["""{"unitPrice":1000.00,"quantity":3}"""]);

        Assert.Contains("<td>1000.00</td><td>3</td>", block.Text, StringComparison.Ordinal);
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
    [InlineData("{{#if (}}")]
    [InlineData("{{ item.amount ")]
    public void APartThatCannotBeParsedIsRefusedWhenThePackageIsPrepared(string source)
    {
        var refusal = Assert.Throws<TemplatePackageException>(() =>
            EngineCases.Prepare(Engine, [(TemplatePart.DocumentStart, source)]));

        Assert.Contains("documentStart", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIncludeNamesADeclaredPartOfThePackage()
    {
        var block = EngineCases.Compose(
            Engine,
            [
                (TemplatePart.DocumentStart, "<p>{{> documentEnd}}</p>"),
                (TemplatePart.DocumentEnd, "the end"),
            ],
            header: "{}",
            records: []);

        Assert.Equal("<p>the end</p>the end", block.Text.Trim());
    }

    [Fact]
    public void AnIncludeOfSomethingThePackageDoesNotDeclareIsRefused()
    {
        Assert.Throws<TemplatePackageException>(() =>
            EngineCases.Compose(Engine, [(TemplatePart.DocumentStart, "{{> ../../etc/passwd}}")], header: "{}", records: []));
    }

    [Fact]
    public void ThePresentationTablePresentsThroughTheHelpersWithTheBlocksCulture()
    {
        var block = EngineCases.Compose(
            Engine,
            [(TemplatePart.Row, "<tr><td>{{format_number item.amount \"N2\"}}</td><td>{{format_date item.date \"dd/MM/yyyy\"}}</td></tr>")],
            header: "{}",
            records: ["""{"amount":1000.00,"date":"2026-10-30T02:30:00Z"}"""]);

        Assert.Contains("<td>1.000,00</td><td>29/10/2026</td>", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBlocksClockIsTheAcceptanceInstantInTheBlocksZone()
    {
        var block = EngineCases.Compose(
            Engine,
            [(TemplatePart.DocumentStart, "{{format_date render.clock \"dd/MM/yyyy HH:mm\"}}")],
            header: "{}",
            records: []);

        Assert.Equal("01/11/2026 23:30", block.Text.Trim());
    }

    [Fact]
    public void AComparisonIsARealBooleanTakenByTheEnginesOwnIf()
    {
        var block = EngineCases.Compose(
            Engine,
            [(TemplatePart.Row, "{{#if (num_gt item.amount \"0\")}}true{{else}}false{{/if}}")],
            header: "{}",
            records: ["""{"amount":1000.00}""", """{"amount":-0.50}""", """{"amount":0}"""]);

        Assert.Equal(["true", "false", "false"], block.Rows.Select(row => block.TextOf(row)));
    }

    [Fact]
    public void ASignPrintsItsTextAndIsNeverACondition()
    {
        var block = EngineCases.Compose(
            Engine,
            [(TemplatePart.Row, "{{num_sign item.amount}}|{{#if (num_sign item.amount)}}truthy{{else}}falsey{{/if}}")],
            header: "{}",
            records: ["""{"amount":-0.50}""", """{"amount":0}"""]);

        // "0" and "-1" are non-empty strings, which every engine reads as true: that is why
        // a sign used as a condition is always the comparison, never num_sign.
        Assert.Equal(["-1|truthy", "0|truthy"], block.Rows.Select(row => block.TextOf(row)).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("{{format_number item.amount}}", "format_number")]
    [InlineData("{{format_number item.amount \"N2\" \"extra\"}}", "format_number")]
    [InlineData("{{num_sign}}", "num_sign")]
    public void AHelperCalledWithTheWrongNumberOfArgumentsIsTheTablesArityRefusal(string source, string function)
    {
        var refusal = Assert.Throws<PresentationRefusedException>(() =>
            EngineCases.Compose(
                Engine,
                [(TemplatePart.Row, source)],
                header: "{}",
                records: ["""{"amount":1000.00}"""]));

        Assert.Equal("TEMPLATE_INVALID", refusal.Failure.Code);
        Assert.Equal("format-string", refusal.Failure.Reason);
        Assert.Equal(function, refusal.Failure.Function);
    }

    [Fact]
    public void TheEngineReportsTheNameAndTheVersionItRuns()
    {
        var adapter = new HandlebarsTemplateAdapter();

        Assert.Equal("handlebars", adapter.Engine);
        Assert.Equal("2.4.3", adapter.Version);
    }
}
