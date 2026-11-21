using System.Globalization;

namespace ReportService.Templates.Tests.Presentation;

/// <summary>
/// The two date forms: a calendar date, which is never converted between zones, and an
/// instant, which is. The accepted forms, the no-offset-means-UTC rule, and the conversion
/// across both directions of a daylight-saving change.
/// </summary>
public sealed class IsoDateTests
{
    private static readonly CultureInfo EsAr = CultureInfo.GetCultureInfo("es-AR");

    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    [Fact]
    public void ADateOnlyValueIsACalendarDate()
    {
        Assert.True(IsoDate.TryClassify("2026-10-29", out var date));

        var calendar = Assert.IsType<IsoDate.CalendarDate>(date);
        Assert.Equal(new DateOnly(2026, 10, 29), calendar.Value);
    }

    [Theory]
    [InlineData("2026-11-02T02:21:56Z", 0)]
    [InlineData("2026-11-02T02:21:56+05:30", 330)]
    [InlineData("2026-11-02T02:21:56-03:00", -180)]
    [InlineData("2026-11-02T02:21:56", 0)]
    [InlineData("2026-11-02T02:21", 0)]
    [InlineData("2026-11-02T02:21:56.123Z", 0)]
    public void AnInstantIsReadWithItsOffsetAndWithoutOne(string text, int offsetMinutes)
    {
        Assert.True(IsoDate.TryClassify(text, out var date));

        var moment = Assert.IsType<IsoDate.Moment>(date);
        Assert.Equal(TimeSpan.FromMinutes(offsetMinutes), moment.Value.Offset);
    }

    [Fact]
    public void TheFractionOfASecondIsKept()
    {
        Assert.True(IsoDate.TryClassify("2026-11-02T02:21:56.1234567+00:00", out var date));

        var moment = Assert.IsType<IsoDate.Moment>(date);
        Assert.Equal(1234567, moment.Value.Ticks % TimeSpan.TicksPerSecond);
    }

    [Theory]
    [InlineData("29/10/2026")]
    [InlineData("2026-02-30")]
    [InlineData("2026-13-01")]
    [InlineData("2026-10-29T")]
    [InlineData("2026-10-29T25:00")]
    [InlineData("2026-10-29T10:00:00.12345678Z")]
    [InlineData("")]
    public void EverythingElseIsNotADate(string text)
    {
        Assert.False(IsoDate.TryClassify(text, out _));
    }

    [Fact]
    public void ANullValueIsNotADate()
    {
        Assert.False(IsoDate.TryClassify(null, out _));
    }

    [Fact]
    public void AnInstantCrossingTheSpringChangeIsShownInTheZoneItReaches()
    {
        var instant = new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero);
        var before = IsoDate.ToZoned(instant, Madrid);
        var after = IsoDate.ToZoned(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), Madrid);

        Assert.Equal(TimeSpan.FromHours(1), before.Offset);
        Assert.Equal(TimeSpan.FromHours(2), after.Offset);
        Assert.Equal("29/03/2026 01:30", before.ToString("dd/MM/yyyy HH:mm", EsAr));
        Assert.Equal("29/03/2026 03:30", after.ToString("dd/MM/yyyy HH:mm", EsAr));
        Assert.Equal("29/03/2026 09:30", IsoDate.ToZoned(instant, Tokyo).ToString("dd/MM/yyyy HH:mm", EsAr));
    }

    [Fact]
    public void AnInstantCrossingTheAutumnChangeIsShownInTheZoneItReaches()
    {
        var before = IsoDate.ToZoned(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), Madrid);
        var after = IsoDate.ToZoned(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), Madrid);

        Assert.Equal(TimeSpan.FromHours(2), before.Offset);
        Assert.Equal(TimeSpan.FromHours(1), after.Offset);
        Assert.Equal("25/10/2026 02:30", before.ToString("dd/MM/yyyy HH:mm", EsAr));
        Assert.Equal("25/10/2026 02:30", after.ToString("dd/MM/yyyy HH:mm", EsAr));
    }

    [Theory]
    [InlineData("2026-03-29")]
    [InlineData("2026-10-25")]
    [InlineData("2026-10-29")]
    public void AChangeDayIsACalendarDateAndNotAnInstant(string text)
    {
        Assert.True(IsoDate.TryClassify(text, out var date));

        var calendar = Assert.IsType<IsoDate.CalendarDate>(date);
        Assert.Equal(text, calendar.Value.ToString(IsoDate.CalendarFormat, CultureInfo.InvariantCulture));
    }
}
