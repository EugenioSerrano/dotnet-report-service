using System.Globalization;
using System.Text.Json;

namespace ReportService.Jobs;

/// <summary>
/// What produced one printed block's presentation, recorded with the block in the same
/// transaction as its commit: the culture, the time zone and the clock the job was accepted
/// with, the engine the version declares and the version that engine runs, and the
/// globalisation data of the process that printed it. It holds settings and version strings,
/// never a value of the consumer's data, so it can be read and logged without exposing a
/// report's content.
/// <para>
/// A row written before the column existed - or by a build that does not name it - carries
/// the one value <see cref="UnrecordedJson"/>, which says truthfully that the build wrote no
/// provenance; <see cref="Parse"/> answers <see langword="null"/> for it, so no reader is
/// handed a half-filled object.
/// </para>
/// </summary>
/// <param name="Culture">The job's culture, as the report is presented in it.</param>
/// <param name="TimeZone">The job's time zone, as a zone the runtime resolved.</param>
/// <param name="Clock">The job's acceptance instant, normalized to UTC.</param>
/// <param name="Engine">The engine the version declares, as the manifest names it.</param>
/// <param name="EngineVersion">The version that engine runs, as the adapter reports it.</param>
/// <param name="IcuVersion">The ICU version of the process that printed the block.</param>
/// <param name="TzDataVersion">The time-zone data version of the process that printed the block.</param>
public sealed record PresentationProvenance(
    string Culture,
    string TimeZone,
    DateTimeOffset Clock,
    string Engine,
    string EngineVersion,
    string IcuVersion,
    string TzDataVersion)
{
    /// <summary>The value a row carries when the build that wrote it recorded no provenance.</summary>
    public const string UnrecordedJson = """{"source":"unrecorded"}""";

    /// <summary>The one member name the value of a build that recorded no provenance carries.</summary>
    public const string UnrecordedSource = "unrecorded";

    /// <summary>The one instant form a recorded clock is written in: UTC, round-trip.</summary>
    public const string ClockFormat = "O";

    /// <summary>This provenance as the column stores it.</summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("culture", Culture);
            writer.WriteString("timeZone", TimeZone);
            writer.WriteString("clock", Clock.ToUniversalTime().ToString(ClockFormat, CultureInfo.InvariantCulture));
            writer.WriteString("engine", Engine);
            writer.WriteString("engineVersion", EngineVersion);
            writer.WriteString("icuVersion", IcuVersion);
            writer.WriteString("tzDataVersion", TzDataVersion);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads a stored value: the complete object, or <see langword="null"/> for the backfill
    /// value. A value that is neither - a partial object, a member of the wrong kind - is an
    /// error of the record rather than a provenance nobody can act on.
    /// </summary>
    /// <param name="json">The stored value.</param>
    /// <exception cref="JsonException">The value is neither the complete object nor the backfill value.</exception>
    public static PresentationProvenance? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The stored presentation is not an object.");
        }

        if (root.TryGetProperty("source", out var source)
            && root.EnumerateObject().Count() == 1
            && source.ValueKind == JsonValueKind.String
            && string.Equals(source.GetString(), UnrecordedSource, StringComparison.Ordinal))
        {
            // The one value the column admits without the seven members - and exactly it: an
            // object that also names a member is a partial provenance, not this value.
            return null;
        }

        return new PresentationProvenance(
            Text(root, "culture"),
            Text(root, "timeZone"),
            DateTimeOffset.Parse(Text(root, "clock"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Text(root, "engine"),
            Text(root, "engineVersion"),
            Text(root, "icuVersion"),
            Text(root, "tzDataVersion"));
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new JsonException($"The stored presentation names no {name}.");
}
