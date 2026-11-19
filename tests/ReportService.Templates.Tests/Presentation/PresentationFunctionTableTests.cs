using System.Globalization;
using System.Reflection;

namespace ReportService.Templates.Tests.Presentation;

/// <summary>
/// The one table of presentation function names: nine names, one delegate each, an
/// engine-neutral signature, and no culture, time zone or clock a template could pass.
/// </summary>
public sealed class PresentationFunctionTableTests
{
    private static readonly PresentationSettings Settings = new(
        CultureInfo.GetCultureInfo("es-AR"),
        TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"),
        new DateTimeOffset(2026, 11, 2, 2, 21, 56, TimeSpan.Zero));

    [Fact]
    public void TheTableHoldsTheNineNames()
    {
        Assert.Equal(PresentationFunctionTable.FunctionCount, PresentationFunctionTable.Names.Count);
        Assert.Equal(
            [
                "format_number",
                "format_date",
                "num_gt",
                "num_ge",
                "num_lt",
                "num_le",
                "num_eq",
                "num_ne",
                "num_sign",
            ],
            PresentationFunctionTable.Names);
    }

    [Fact]
    public void EveryNameHasOneDelegateOfItsOwn()
    {
        var functions = PresentationFunctionTable.For(Settings);

        Assert.Equal(PresentationFunctionTable.FunctionCount, functions.Count);
        Assert.All(PresentationFunctionTable.Names, name => Assert.True(functions.ContainsKey(name)));
        Assert.All(functions.Values, function => Assert.NotNull(function));
        Assert.Equal(PresentationFunctionTable.FunctionCount, functions.Values.Distinct().Count());
    }

    [Fact]
    public void TheSignatureIsOneArgumentListAndNothingElse()
    {
        foreach (var function in PresentationFunctionTable.For(Settings).Values)
        {
            var parameters = function.Method.GetParameters();
            var parameter = Assert.Single(parameters);
            Assert.Equal(typeof(IReadOnlyList<object?>), parameter.ParameterType);
        }
    }

    [Fact]
    public void NoFunctionAcceptsACultureAZoneOrAClock()
    {
        var forbidden = new[] { typeof(CultureInfo), typeof(TimeZoneInfo), typeof(DateTimeOffset), typeof(TimeProvider) };

        var members = typeof(PresentationFunctions).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.NotEmpty(members);
        foreach (var member in members.OfType<MethodBase>())
        {
            Assert.DoesNotContain(member.GetParameters(), parameter => forbidden.Contains(parameter.ParameterType));
        }
    }

    [Fact]
    public void AFunctionCalledThroughTheTablePresentsWithTheBlockSettings()
    {
        var functions = PresentationFunctionTable.For(Settings);

        var number = Assert.IsType<PresentationResult.Text>(functions[PresentationFunctionTable.FormatNumberName](["1000.00", "N2"]));
        var date = Assert.IsType<PresentationResult.Text>(functions[PresentationFunctionTable.FormatDateName](["2026-11-02T02:30:00Z", "dd/MM/yyyy"]));
        var sign = Assert.IsType<PresentationResult.Text>(functions[PresentationFunctionTable.NumSignName](["-0.50"]));
        var comparison = Assert.IsType<PresentationResult.Flag>(functions[PresentationFunctionTable.NumLessThanName](["1", "2"]));

        Assert.Equal("1.000,00", number.Value);
        Assert.Equal("01/11/2026", date.Value);
        Assert.Equal("-1", sign.Value);
        Assert.True(comparison.Value);
    }

    [Fact]
    public void AFunctionCalledWithTheWrongNumberOfArgumentsIsRefused()
    {
        var functions = PresentationFunctionTable.For(Settings);

        var refused = Assert.IsType<PresentationResult.Refused>(functions[PresentationFunctionTable.FormatNumberName](["1000.00"]));

        Assert.Equal(PresentationRefusal.BadFormat, refused.Reason);
    }
}
