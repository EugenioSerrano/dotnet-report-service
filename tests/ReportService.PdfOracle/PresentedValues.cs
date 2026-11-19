using System.Globalization;
using System.Text.Json.Nodes;

namespace ReportService.PdfOracle;

/// <summary>One field a package prints through a presentation function, as its declaration names it.</summary>
/// <param name="Column">The declared column the field prints in.</param>
/// <param name="Member">The record member the field is computed from.</param>
/// <param name="Function">The function the package printed it with: <c>format_number</c> or <c>format_date</c>.</param>
/// <param name="Format">The format the package asked for.</param>
internal sealed record PresentedField(string Column, string Member, string Function, string Format);

/// <summary>
/// The values a package prints through a presentation function, and the job's culture and zone
/// they were presented with. The judge computes each expected text itself, from the snapshot and
/// this declaration alone: it never reads the product's presentation code, so a defect shared by
/// both sides of the composition cannot make an expectation agree with it. A date-only value is a
/// calendar date and is never converted; an instant is shown in the declared zone.
/// </summary>
internal sealed class PresentedValues
{
    private const string CalendarFormat = "yyyy-MM-dd";

    private static readonly string[] InstantFormats =
    [
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    private PresentedValues(
        CultureInfo culture,
        TimeZoneInfo timeZone,
        DateTimeOffset? clock,
        string? clockFormat,
        IReadOnlyList<PresentedField> fields)
    {
        Culture = culture;
        TimeZone = timeZone;
        Clock = clock;
        ClockFormat = clockFormat;
        Fields = fields;
    }

    /// <summary>The culture the printed text must be in.</summary>
    public CultureInfo Culture { get; }

    /// <summary>The time zone an instant is shown in.</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>The acceptance instant the document's close prints, when the declaration names one.</summary>
    public DateTimeOffset? Clock { get; }

    /// <summary>The format the acceptance instant is printed with.</summary>
    public string? ClockFormat { get; }

    /// <summary>The presented fields of the detail rows.</summary>
    public IReadOnlyList<PresentedField> Fields { get; }

    /// <summary>Whether the declaration names any presented value at all.</summary>
    public bool Applies => Fields.Count > 0 || Clock is not null;

    /// <summary>The presented fields computed from a number.</summary>
    public IEnumerable<PresentedField> NumericFields =>
        Fields.Where(presented => string.Equals(presented.Function, "format_number", StringComparison.Ordinal));

    /// <summary>The expected text of <paramref name="field"/> for <paramref name="record"/>, or <see langword="null"/> when the value is not one the rule reads.</summary>
    public string? Expected(FixtureRecord record, PresentedField field)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(field);
        var value = record.Member(field.Member);
        return field.Function switch
        {
            "format_number" => Number(value, field.Format),
            "format_date" => Date(value, field.Format),
            _ => throw new InvalidDataException($"The declaration names no presentation function '{field.Function}'."),
        };
    }

    /// <summary>The expected text of the acceptance instant, or <see langword="null"/> when the declaration names none.</summary>
    public string? ExpectedClock()
    {
        if (Clock is null || ClockFormat is null)
        {
            return null;
        }

        return TimeZoneInfo.ConvertTime(Clock.Value, TimeZone).ToString(ClockFormat, Culture);
    }

    /// <summary>Reads the <c>presented</c> element of a declaration, or <see langword="null"/> when it names none.</summary>
    public static PresentedValues? Parse(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root["presented"] is not JsonObject presented)
        {
            return null;
        }

        var cultureName = Text(presented, "culture");
        var timeZoneName = Text(presented, "timeZone");
        CultureInfo culture;
        TimeZoneInfo timeZone;
        try
        {
            culture = CultureInfo.GetCultureInfo(cultureName);
        }
        catch (CultureNotFoundException)
        {
            throw new InvalidDataException($"The declaration names no culture '{cultureName}'.");
        }

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneName);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new InvalidDataException($"The declaration names no time zone '{timeZoneName}'.");
        }
        catch (InvalidTimeZoneException)
        {
            throw new InvalidDataException($"The declaration names no usable time zone '{timeZoneName}'.");
        }

        DateTimeOffset? clock = null;
        var clockFormat = OptionalText(presented, "clockFormat");
        if (presented["clock"] is JsonValue instant)
        {
            if (!DateTimeOffset.TryParse(instant.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment))
            {
                throw new InvalidDataException("The declaration's acceptance instant is not an ISO 8601 instant.");
            }

            clock = moment;
            if (clockFormat is null)
            {
                throw new InvalidDataException("The declaration names an acceptance instant and no format for it.");
            }
        }

        var fields = new List<PresentedField>();
        if (presented["fields"] is JsonArray declared)
        {
            foreach (var entry in declared)
            {
                var field = entry as JsonObject
                    ?? throw new InvalidDataException("A presented field is not a JSON object.");
                fields.Add(new PresentedField(
                    Text(field, "column"),
                    Text(field, "member"),
                    Text(field, "function"),
                    Text(field, "format")));
            }
        }

        return new PresentedValues(culture, timeZone, clock, clockFormat, fields);
    }

    private string? Number(string value, string format)
    {
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                return null;
            }
        }

        try
        {
            return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                ? number.ToString(format, Culture)
                : null;
        }
        catch (FormatException)
        {
            throw new InvalidDataException($"The declaration's format '{format}' cannot present a number.");
        }
    }

    private string? Date(string value, string format)
    {
        try
        {
            if (DateOnly.TryParseExact(value, CalendarFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var calendar))
            {
                return calendar.ToString(format, Culture);
            }

            return DateTimeOffset.TryParseExact(value, InstantFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                ? TimeZoneInfo.ConvertTime(moment, TimeZone).ToString(format, Culture)
                : null;
        }
        catch (FormatException)
        {
            throw new InvalidDataException($"The declaration's format '{format}' cannot present a date.");
        }
    }

    private static string Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
            ? text
            : throw new InvalidDataException($"The declaration's presented values hold no '{name}'.");

    private static string? OptionalText(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
}
