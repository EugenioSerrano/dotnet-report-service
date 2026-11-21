using System.Globalization;

namespace ReportService.Templates.Tests.Presentation;

/// <summary>
/// The one resolution of a block's presentation: the three values when the job carries
/// them, a typed refusal for each member that is absent, an unknown name refused rather
/// than replaced by a default, and a clock normalised to UTC. No clock is read.
/// </summary>
public sealed class PresentationSettingsResolverTests
{
    private static readonly DateTimeOffset Accepted = new(2026, 11, 2, 8, 30, 3, TimeSpan.FromHours(-3));

    [Fact]
    public void TheJobsThreeValuesAreResolvedOnce()
    {
        var settings = PresentationSettingsResolver.Resolve(new PresentationValues("es-AR", "America/Argentina/Buenos_Aires", Accepted));

        Assert.Equal("es-AR", settings.Culture.Name);
        Assert.Equal("America/Argentina/Buenos_Aires", settings.TimeZone.Id);
        Assert.Equal(Accepted.ToUniversalTime(), settings.Clock);
    }

    [Fact]
    public void AMissingCultureIsRefused()
    {
        var refusal = Assert.Throws<PresentationUnavailable>(() =>
            PresentationSettingsResolver.Resolve(new PresentationValues(null, "UTC", Accepted)));

        Assert.Equal(PresentationUnavailable.Fields.Culture, refusal.Field);
        Assert.DoesNotContain("UTC", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingTimeZoneIsRefused()
    {
        var refusal = Assert.Throws<PresentationUnavailable>(() =>
            PresentationSettingsResolver.Resolve(new PresentationValues("es-AR", "  ", Accepted)));

        Assert.Equal(PresentationUnavailable.Fields.TimeZone, refusal.Field);
    }

    [Fact]
    public void AMissingClockIsRefused()
    {
        var refusal = Assert.Throws<PresentationUnavailable>(() =>
            PresentationSettingsResolver.Resolve(new PresentationValues("es-AR", "UTC", null)));

        Assert.Equal(PresentationUnavailable.Fields.Clock, refusal.Field);
    }

    [Fact]
    public void ACultureTheRuntimeDoesNotKnowIsRefusedRatherThanDefaulted()
    {
        var refusal = Assert.Throws<PresentationUnavailable>(() =>
            PresentationSettingsResolver.Resolve(new PresentationValues("xx-YY", "UTC", Accepted)));

        Assert.Equal(PresentationUnavailable.Fields.Culture, refusal.Field);
    }

    [Fact]
    public void AZoneTheRuntimeDoesNotKnowIsRefusedRatherThanDefaulted()
    {
        var refusal = Assert.Throws<PresentationUnavailable>(() =>
            PresentationSettingsResolver.Resolve(new PresentationValues("es-AR", "Mars/Olympus_Mons", Accepted)));

        Assert.Equal(PresentationUnavailable.Fields.TimeZone, refusal.Field);
    }

    [Fact]
    public void AClockThatArrivedWithAnOffsetIsStoredInUtc()
    {
        var settings = PresentationSettingsResolver.Resolve(new PresentationValues("es-AR", "UTC", Accepted));

        Assert.Equal(TimeSpan.Zero, settings.Clock.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 11, 30, 3, TimeSpan.Zero), settings.Clock);
    }

    [Fact]
    public void TheResolutionReadsNoClock()
    {
        var members = typeof(PresentationSettingsResolver)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly);

        Assert.NotEmpty(members);
        Assert.DoesNotContain(members, method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(TimeProvider)));
        Assert.DoesNotContain(members, method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(System.Globalization.CultureInfo)));
    }

    [Theory]
    [InlineData("es-AR")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    public void TheAcceptedCulturesResolveToThemselves(string name)
    {
        var settings = PresentationSettingsResolver.Resolve(new PresentationValues(name, "UTC", Accepted));

        Assert.Equal(name, settings.Culture.Name);
        Assert.Equal(CultureInfo.GetCultureInfo(name).Name, settings.Culture.Name);
    }
}
