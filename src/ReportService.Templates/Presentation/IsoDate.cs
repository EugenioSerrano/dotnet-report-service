using System.Globalization;
using System.Text.RegularExpressions;

namespace ReportService.Templates;

/// <summary>
/// The two date forms a template can present: a calendar date, which carries no time and
/// no zone, and an instant.
/// <para>
/// A calendar date is never converted between zones, so <c>2026-10-29</c> prints the same
/// day in every zone of the world. An instant is converted to the job's zone before it is
/// formatted. A text that carries a time but no offset is an instant in UTC.
/// </para>
/// </summary>
public abstract partial record IsoDate
{
    private IsoDate()
    {
    }

    /// <summary>A calendar date: it prints the same day in every time zone.</summary>
    /// <param name="Value">The date as it arrived.</param>
    public sealed record CalendarDate(DateOnly Value) : IsoDate;

    /// <summary>An instant, carrying the offset it arrived with.</summary>
    /// <param name="Value">The instant as it arrived.</param>
    public sealed record Moment(DateTimeOffset Value) : IsoDate;

    /// <summary>The one accepted date-only form.</summary>
    public const string CalendarFormat = "yyyy-MM-dd";

    /// <summary>The forms an instant may arrive in; the fraction is optional and up to seven digits.</summary>
    private static readonly string[] InstantFormats =
    [
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    /// <summary>Reads <paramref name="text"/> as a calendar date or as an instant.</summary>
    /// <param name="text">The value as the render context holds it.</param>
    /// <param name="value">The classified value, when the text is one of the accepted forms.</param>
    public static bool TryClassify(string? text, out IsoDate? value)
    {
        value = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (CalendarShape().IsMatch(text))
        {
            if (!DateOnly.TryParseExact(text, CalendarFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return false;
            }

            value = new CalendarDate(date);
            return true;
        }

        if (!InstantShape().IsMatch(text))
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(text, InstantFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment))
        {
            return false;
        }

        value = new Moment(moment);
        return true;
    }

    /// <summary>The instant shown in <paramref name="zone"/>.</summary>
    /// <param name="instant">The instant as it arrived.</param>
    /// <param name="zone">The job's time zone.</param>
    public static DateTimeOffset ToZoned(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(instant, zone);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CalendarShape();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex InstantShape();
}
