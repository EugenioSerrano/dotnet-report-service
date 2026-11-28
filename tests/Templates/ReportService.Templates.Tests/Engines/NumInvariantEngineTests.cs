namespace ReportService.Templates.Tests;

/// <summary>
/// The culture-invariant number writer in each engine: the same attribute text under a
/// comma-decimal culture and under en-US, an engine's own arithmetic result written
/// invariantly, and a refusal naming the function.
/// </summary>
public sealed class NumInvariantEngineTests
{
    private const string Values = """{"price":"41.82","negative":"-0.50","whole":"1000.00","mid":"2.345"}""";

    public static TheoryData<string, string> EnginesAndCultures => new()
    {
        { ScribanTemplateAdapter.EngineName, "es-AR" },
        { ScribanTemplateAdapter.EngineName, "en-US" },
        { HandlebarsTemplateAdapter.EngineName, "es-AR" },
        { HandlebarsTemplateAdapter.EngineName, "en-US" },
        { FluidTemplateAdapter.EngineName, "es-AR" },
        { FluidTemplateAdapter.EngineName, "en-US" },
    };

    public static TheoryData<string, string> NotInvariantNumbers => new()
    {
        { ScribanTemplateAdapter.EngineName, "{{ num_invariant item.comma | html.escape }}" },
        { HandlebarsTemplateAdapter.EngineName, "{{num_invariant item.comma}}" },
        { FluidTemplateAdapter.EngineName, "{{ item.comma | num_invariant }}" },
        { ScribanTemplateAdapter.EngineName, "{{ num_invariant item.missing | html.escape }}" },
        { HandlebarsTemplateAdapter.EngineName, "{{num_invariant item.missing}}" },
        { FluidTemplateAdapter.EngineName, "{{ item.missing | num_invariant }}" },
        { ScribanTemplateAdapter.EngineName, "{{ num_invariant (1.0 / 0) | html.escape }}" },
        { ScribanTemplateAdapter.EngineName, "{{ num_invariant true | html.escape }}" },
    };

    public static TheoryData<string, string> BadDecimals => new()
    {
        { ScribanTemplateAdapter.EngineName, "{{ num_invariant item.price 29 | html.escape }}" },
        { HandlebarsTemplateAdapter.EngineName, "{{num_invariant item.price \"x\"}}" },
        { FluidTemplateAdapter.EngineName, "{{ item.price | num_invariant: -1 }}" },
        { HandlebarsTemplateAdapter.EngineName, "{{num_invariant item.price 1 2}}" },
        { FluidTemplateAdapter.EngineName, "{{ item.price | num_invariant: 1, 2 }}" },
    };

    [Theory]
    [MemberData(nameof(EnginesAndCultures))]
    public async Task TheAttributeTextIsTheSameInEveryCulture(string engine, string culture)
    {
        var row = engine switch
        {
            ScribanTemplateAdapter.EngineName =>
                "<rect height=\"{{ num_invariant item.price | html.escape }}\" y=\"{{ num_invariant item.negative | html.escape }}\" width=\"{{ num_invariant item.whole | html.escape }}\" x=\"{{ num_invariant item.mid 2 | html.escape }}\"/>",
            HandlebarsTemplateAdapter.EngineName =>
                "<rect height=\"{{num_invariant item.price}}\" y=\"{{num_invariant item.negative}}\" width=\"{{num_invariant item.whole}}\" x=\"{{num_invariant item.mid 2}}\"/>",
            _ =>
                "<rect height=\"{{ item.price | num_invariant }}\" y=\"{{ item.negative | num_invariant }}\" width=\"{{ item.whole | num_invariant }}\" x=\"{{ item.mid | num_invariant: 2 }}\"/>",
        };

        var block = await EngineCases.Compose(engine, [(TemplatePart.Row, row)], header: "{}", records: [Values], settings: Settings(culture));

        Assert.Equal("<rect height=\"41.82\" y=\"-0.50\" width=\"1000.00\" x=\"2.35\"/>", block.TextOf(block.Rows.Single()));
    }

    [Fact]
    public async Task ScribansOwnArithmeticIsWrittenInvariantly()
    {
        var block = await EngineCases.Compose(
            ScribanTemplateAdapter.EngineName,
            [(TemplatePart.Row, "{{ num_invariant (1 / 4) | html.escape }}|{{ num_invariant (7 * 3) | html.escape }}")],
            header: "{}",
            records: ["{}"]);

        Assert.Equal("0.25|21", block.TextOf(block.Rows.Single()));
    }

    [Fact]
    public async Task LiquidsOwnArithmeticIsWrittenInvariantlyUnderACommaCulture()
    {
        var block = await EngineCases.Compose(
            FluidTemplateAdapter.EngineName,
            [(TemplatePart.Row, "{{ item.price | times: 2 }}|{{ item.price | times: 2 | num_invariant }}")],
            header: "{}",
            records: [Values]);

        // The engine's own output is in the job's culture; the host function is not.
        Assert.Equal("83,64|83.64", block.TextOf(block.Rows.Single()));
    }

    [Theory]
    [MemberData(nameof(NotInvariantNumbers))]
    public async Task AValueThatIsNotAnInvariantNumberIsRefusedNamingTheFunction(string engine, string row)
    {
        var refusal = await Assert.ThrowsAsync<PresentationRefusedException>(() => EngineCases.Compose(
            engine,
            [(TemplatePart.Row, row)],
            header: "{}",
            records: ["""{"comma":"41,82"}"""]));

        Assert.Equal("num_invariant", refusal.Failure.Function);
    }

    [Theory]
    [MemberData(nameof(BadDecimals))]
    public async Task DecimalsOutsideTheRangeOrTooManyArgumentsAreATemplateRefusal(string engine, string row)
    {
        var refusal = await Assert.ThrowsAsync<PresentationRefusedException>(() => EngineCases.Compose(
            engine,
            [(TemplatePart.Row, row)],
            header: "{}",
            records: [Values]));

        Assert.Equal("TEMPLATE_INVALID", refusal.Failure.Code);
        Assert.Equal("num_invariant", refusal.Failure.Function);
    }

    private static PresentationSettings Settings(string culture) =>
        culture == "es-AR" ? EngineCases.BuenosAires : EngineCases.Tokyo;
}
