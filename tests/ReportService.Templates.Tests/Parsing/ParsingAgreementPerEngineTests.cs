namespace ReportService.Templates.Tests.Parsing;

/// <summary>
/// The take's parse and the render's prepare answer the same verdict on the same text for
/// the two engines whose render half was written beside them: a part the validator accepts
/// is one the adapter can prepare, and one it refuses is refused in both halves.
/// </summary>
public sealed class ParsingAgreementPerEngineTests
{
    public static TheoryData<string, string> Corpus => new()
    {
        { HandlebarsTemplateAdapter.EngineName, "<tr><td>{{ item.quantity }}</td></tr>" },
        { HandlebarsTemplateAdapter.EngineName, "<html><body>" },
        { HandlebarsTemplateAdapter.EngineName, "{{ item.amount " },
        { FluidTemplateAdapter.EngineName, "<tr><td>{{ item.quantity }}</td></tr>" },
        { FluidTemplateAdapter.EngineName, "<html><body>" },
        { FluidTemplateAdapter.EngineName, "{% if %}text{% endif %}" },
        { FluidTemplateAdapter.EngineName, "{% assign total = item.quantity %}{{ total }}" },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void TheTakeAndTheRenderAnswerTheSameVerdictForEveryPart(string engine, string source)
    {
        Assert.Equal(ValidatorAccepts(engine, source), AdapterAccepts(engine, source));
    }

    [Fact]
    public void BothHalvesOfEachEngineCommandTheSamePartText()
    {
        const string part = "<tr><td>{{ item.quantity }}</td></tr>";

        Assert.True(ValidatorAccepts(HandlebarsTemplateAdapter.EngineName, part));
        Assert.True(AdapterAccepts(HandlebarsTemplateAdapter.EngineName, part));
        Assert.True(ValidatorAccepts(FluidTemplateAdapter.EngineName, part));
        Assert.True(AdapterAccepts(FluidTemplateAdapter.EngineName, part));
    }

    private static bool ValidatorAccepts(string engine, string source)
    {
        ITemplateEngineValidator validator = engine == HandlebarsTemplateAdapter.EngineName
            ? new HandlebarsTemplateValidator()
            : new FluidTemplateValidator();
        return validator.Parse("html/row.html" + validator.PartExtension, source) is PartParseResult.Parsed;
    }

    private static bool AdapterAccepts(string engine, string source)
    {
        try
        {
            _ = EngineCases.Prepare(engine, [(TemplatePart.DocumentStart, source)]);
            return true;
        }
        catch (TemplatePackageException)
        {
            return false;
        }
    }
}
