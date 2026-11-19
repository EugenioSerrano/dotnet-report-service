using Fluid;

namespace ReportService.Templates.Tests.Parsing;

/// <summary>
/// The one Liquid parser the product makes: the library's defaults, used by the take's
/// validator and by the render's adapter, so the same text is accepted in both halves.
/// </summary>
public sealed class FluidParsingTests
{
    [Fact]
    public void APartTheEngineAcceptsComesBackAsATemplate()
    {
        var parser = FluidParsing.Create();

        Assert.True(parser.TryParse("{{ item.quantity }}", out var template, out var error));
        Assert.NotNull(template);
        Assert.Null(error);
    }

    [Fact]
    public void APartTheEngineRefusesCarriesItsPosition()
    {
        var parser = FluidParsing.Create();

        Assert.False(parser.TryParse("{% for x in %}", out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void EveryCallAnswersItsOwnParser()
    {
        Assert.NotSame(FluidParsing.Create(), FluidParsing.Create());
    }
}
