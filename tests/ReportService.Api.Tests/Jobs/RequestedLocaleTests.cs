using ReportService.Api.Http;
using ReportService.Api.Jobs;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The presentation a request may name at acceptance: a culture and a time zone,
/// either of which may be absent. A value is judged by the runtime's own tables — a
/// culture the runtime does not have and a zone it cannot build are both values the
/// service could never honour, so they are refused at once, each on its own, with
/// the field named and the value never echoed. The values that pass are carried on
/// exactly as the request wrote them.
/// </summary>
public sealed class RequestedLocaleTests
{
    /// <summary>Tags the runtime of every supported deployment resolves.</summary>
    public static TheoryData<string, string> Resolvable => new()
    {
        { "es-AR", "America/Argentina/Buenos_Aires" },
        { "en-GB", "Europe/Madrid" },
        { "en-us", "UTC" },
    };

    [Fact]
    public void NoValueNamedIsAcceptedWithNone()
    {
        var accepted = Assert.IsType<RequestedLocale.Result.Accepted>(RequestedLocale.TryCreate(null, null));

        Assert.Null(accepted.Locale.Culture);
        Assert.Null(accepted.Locale.TimeZone);
    }

    [Theory]
    [MemberData(nameof(Resolvable))]
    public void EachValueTheRuntimeHasIsCarriedAsWritten(string culture, string timeZone)
    {
        var accepted = Assert.IsType<RequestedLocale.Result.Accepted>(RequestedLocale.TryCreate(culture, timeZone));

        // A tag is kept in the spelling the request used: a reader resolves it whatever
        // its case, so folding it would only invent a second spelling.
        Assert.Equal(culture, accepted.Locale.Culture);
        Assert.Equal(timeZone, accepted.Locale.TimeZone);
    }

    [Fact]
    public void ACultureAloneIsCarriedWithNoZone()
    {
        var accepted = Assert.IsType<RequestedLocale.Result.Accepted>(RequestedLocale.TryCreate("es-AR", null));

        Assert.Equal("es-AR", accepted.Locale.Culture);
        Assert.Null(accepted.Locale.TimeZone);
    }

    [Fact]
    public void AZoneAloneIsCarriedWithNoCulture()
    {
        var accepted = Assert.IsType<RequestedLocale.Result.Accepted>(
            RequestedLocale.TryCreate(null, "America/Argentina/Buenos_Aires"));

        Assert.Null(accepted.Locale.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", accepted.Locale.TimeZone);
    }

    [Fact]
    public void ACultureTheRuntimeDoesNotHaveIsRefusedNamingTheFieldAndNotTheValue()
    {
        const string unknown = "xx-notreal";

        var problem = Assert.IsType<RequestedLocale.Result.Rejected>(
            RequestedLocale.TryCreate(unknown, "Europe/Madrid")).Problem;

        Assert.Equal(RequestInvalidDetails.InvalidFormatReason, problem.Reason);
        Assert.Equal("render.culture", problem.Field);
        Assert.Equal(RequestedLocale.UnsupportedCultureDetail, problem.Detail);
        Assert.DoesNotContain(unknown, problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AZoneTheRuntimeCannotBuildIsRefusedNamingTheFieldAndNotTheValue()
    {
        const string unknown = "Mars/Olympus";

        var problem = Assert.IsType<RequestedLocale.Result.Rejected>(
            RequestedLocale.TryCreate("es-AR", unknown)).Problem;

        Assert.Equal(RequestInvalidDetails.InvalidFormatReason, problem.Reason);
        Assert.Equal("render.timeZone", problem.Field);
        Assert.Equal(RequestedLocale.UnsupportedTimeZoneDetail, problem.Detail);
        Assert.DoesNotContain(unknown, problem.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xx-notreal", "Europe/Madrid", "render.culture")]
    [InlineData("es-AR", "Mars/Olympus", "render.timeZone")]
    [InlineData("xx-notreal", "Mars/Olympus", "render.culture")]
    public void EachValueIsJudgedOnItsOwnAndTheFirstUnusableOneIsNamed(
        string culture,
        string timeZone,
        string field)
    {
        var problem = Assert.IsType<RequestedLocale.Result.Rejected>(
            RequestedLocale.TryCreate(culture, timeZone)).Problem;

        Assert.Equal(field, problem.Field);
    }
}
