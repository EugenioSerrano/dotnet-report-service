using System.Globalization;

namespace ReportService.Templates;

/// <summary>
/// Resolves the one presentation a block is rendered with: the job's culture, its time
/// zone and its clock, read once before anything is composed.
/// <para>
/// An absent value, or a culture or zone name the runtime does not know, is a typed
/// refusal. There is no fallback to the package's declared defaults or to the process's
/// own culture: either would let a report drift from what the job was accepted with.
/// </para>
/// </summary>
public static class PresentationSettingsResolver
{
    /// <summary>Resolves <paramref name="values"/> into the settings one block renders with.</summary>
    /// <param name="values">The job's raw values.</param>
    /// <exception cref="PresentationUnavailable">A value is absent or its name is unknown.</exception>
    public static PresentationSettings Resolve(PresentationValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (string.IsNullOrWhiteSpace(values.Culture))
        {
            throw new PresentationUnavailable(PresentationUnavailable.Fields.Culture);
        }

        if (string.IsNullOrWhiteSpace(values.TimeZone))
        {
            throw new PresentationUnavailable(PresentationUnavailable.Fields.TimeZone);
        }

        if (values.Clock is not { } clock)
        {
            throw new PresentationUnavailable(PresentationUnavailable.Fields.Clock);
        }

        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(values.Culture);
        }
        catch (CultureNotFoundException)
        {
            throw new PresentationUnavailable(PresentationUnavailable.Fields.Culture);
        }

        if (!KnownCultures.Value.Contains(culture.Name))
        {
            // A name the runtime does not know is not an error there: it answers with a
            // culture whose data is the invariant one. Presenting with it would silently
            // print dates and numbers in no culture at all, so it is refused here.
            throw new PresentationUnavailable(PresentationUnavailable.Fields.Culture);
        }

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(values.TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new PresentationUnavailable(PresentationUnavailable.Fields.TimeZone);
        }
        catch (InvalidTimeZoneException)
        {
            throw new PresentationUnavailable(PresentationUnavailable.Fields.TimeZone);
        }

        return new PresentationSettings(culture, zone, clock.ToUniversalTime());
    }

    /// <summary>The cultures the runtime itself knows, so a name it would invent is refused.</summary>
    private static readonly Lazy<HashSet<string>> KnownCultures = new(
        () =>
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var known in CultureInfo.GetCultures(CultureTypes.AllCultures))
            {
                names.Add(known.Name);
            }

            return names;
        });
}
