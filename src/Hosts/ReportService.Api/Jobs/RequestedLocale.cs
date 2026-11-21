using System.Globalization;
using ReportService.Api.Http;

namespace ReportService.Api.Jobs;

/// <summary>
/// The presentation a request may name at acceptance: the culture the report's dates,
/// numbers and texts are written in, and the time zone they are read as. Either value
/// may be absent, and an absent one is supplied by the template the job takes. A value
/// is judged by the platform's own tables, because those are the tables the render
/// reads: a culture the runtime does not have and a zone it cannot build are values the
/// service could never honour, so the request is refused before a job exists. What
/// passes is carried exactly as the request wrote it — a tag is case-insensitive to
/// every reader, so folding it would only invent a second spelling.
/// </summary>
/// <param name="Culture">The culture the request named, as written; null when it named none.</param>
/// <param name="TimeZone">The time zone the request named, as written; null when it named none.</param>
public sealed record RequestedLocale(string? Culture, string? TimeZone)
{
    /// <summary>What an answer says when the culture is not one the service's runtime has.</summary>
    public const string UnsupportedCultureDetail =
        "The field 'render.culture' is not a culture the service supports.";

    /// <summary>What an answer says when the time zone is not one the service's runtime can build.</summary>
    public const string UnsupportedTimeZoneDetail =
        "The field 'render.timeZone' is not a time zone the service supports.";

    /// <summary>
    /// Reads the two values a request named. Each is decided on its own, and the first one
    /// the runtime cannot resolve refuses the whole request, naming that field and never the
    /// value. The wire shape of the members — present, a string, of a bounded printable form —
    /// is the request reader's; what arrives here is a value of that shape or absent.
    /// </summary>
    /// <param name="culture">The culture the request named, as written; null when it named none.</param>
    /// <param name="timeZone">The time zone the request named, as written; null when it named none.</param>
    public static Result TryCreate(string? culture, string? timeZone)
    {
        if (culture is not null && !IsCulture(culture))
        {
            return new Result.Rejected(
                InvalidRequestBody.InvalidFormat(CreateReportJobRequestReader.RenderCulturePath, UnsupportedCultureDetail));
        }

        if (timeZone is not null && !IsTimeZone(timeZone))
        {
            return new Result.Rejected(
                InvalidRequestBody.InvalidFormat(CreateReportJobRequestReader.RenderTimeZonePath, UnsupportedTimeZoneDetail));
        }

        return new Result.Accepted(new RequestedLocale(culture, timeZone));
    }

    // The runtime's own table decides, so the value the service accepts is one it can also
    // render. A name it does not know and a name it cannot map are both a refusal.
    private static bool IsCulture(string culture)
    {
        try
        {
            _ = CultureInfo.GetCultureInfo(culture);
            return true;
        }
        catch (ArgumentException)
        {
            // CultureNotFoundException is the runtime's answer for a name it does not have;
            // any other argument fault is a name it cannot read, which is refused the same way.
            return false;
        }
    }

    // An IANA identifier the runtime can build a zone from; a corrupt zone is no better than
    // an unknown one for a report that must be printed in it.
    private static bool IsTimeZone(string timeZone)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>The outcome of judging the presentation a request named.</summary>
    public abstract record Result
    {
        private Result()
        {
        }

        /// <summary>Every value named is one the service can present the report in.</summary>
        /// <param name="Locale">The two values, as the request wrote them.</param>
        public sealed record Accepted(RequestedLocale Locale) : Result;

        /// <summary>A value named cannot be honoured; the condition is the request's to correct.</summary>
        /// <param name="Problem">The one condition the answer reports.</param>
        public sealed record Rejected(InvalidRequestBody Problem) : Result;
    }
}
