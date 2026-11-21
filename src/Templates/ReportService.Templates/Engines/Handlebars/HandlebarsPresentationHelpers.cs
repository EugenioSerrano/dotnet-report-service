using System.Globalization;
using HandlebarsDotNet;

namespace ReportService.Templates;

/// <summary>
/// The presentation table as Handlebars sees it: one helper per name, registered on the
/// environment a package is prepared with. It is the only place Handlebars' own helper API
/// meets the table.
/// <para>
/// A prepared package is shared between blocks and jobs, so a helper is not bound to one
/// block's settings when it is registered. It reads the settings the single resolution wrote
/// into the rendering model - the same values the template prints as <c>render.culture</c>,
/// <c>render.timeZone</c> and <c>render.clock</c> - and builds the one table from them, so a
/// helper and the text a template prints can never disagree, and a package prepared for one
/// job never presents another job's culture.
/// </para>
/// </summary>
public static class HandlebarsPresentationHelpers
{
    /// <summary>Registers the nine names on <paramref name="handlebars"/>.</summary>
    /// <param name="handlebars">The environment the package's parts are compiled in.</param>
    public static void Register(IHandlebars handlebars)
    {
        ArgumentNullException.ThrowIfNull(handlebars);
        handlebars.RegisterHelper(PresentationFunctionTable.FormatNumberName, (HandlebarsReturnHelper)((context, arguments) => Answer(context, arguments, PresentationFunctionTable.FormatNumberName)));
        handlebars.RegisterHelper(PresentationFunctionTable.FormatDateName, (HandlebarsReturnHelper)((context, arguments) => Answer(context, arguments, PresentationFunctionTable.FormatDateName)));
        handlebars.RegisterHelper(PresentationFunctionTable.NumGreaterThanName, (HandlebarsReturnHelper)((context, arguments) => Answer(context, arguments, PresentationFunctionTable.NumGreaterThanName)));
        handlebars.RegisterHelper(PresentationFunctionTable.NumGreaterOrEqualName, (HandlebarsReturnHelper)((context, arguments) => Answer(context, arguments, PresentationFunctionTable.NumGreaterOrEqualName)));
        handlebars.RegisterHelper(PresentationFunctionTable.NumLessThanName, (HandlebarsReturnHelper)((context, arguments) => Answer(context, arguments, PresentationFunctionTable.NumLessThanName)));
        handlebars.RegisterHelper(PresentationFunctionTable.NumLessOrEqualName, (HandlebarsReturnHelper)((context, arguments) => Answer(context, arguments, PresentationFunctionTable.NumLessOrEqualName)));
        handlebars.RegisterHelper(PresentationFunctionTable.NumEqualName, (HandlebarsReturnHelper)((context, arguments) => Answer(context, arguments, PresentationFunctionTable.NumEqualName)));
        handlebars.RegisterHelper(PresentationFunctionTable.NumNotEqualName, (HandlebarsReturnHelper)((context, arguments) => Answer(context, arguments, PresentationFunctionTable.NumNotEqualName)));
        handlebars.RegisterHelper(PresentationFunctionTable.NumSignName, (HandlebarsReturnHelper)((context, arguments) => Answer(context, arguments, PresentationFunctionTable.NumSignName)));
    }

    /// <summary>
    /// Answers one call with the table's own function. Every argument the template wrote is
    /// passed on, however many it wrote: a call with the wrong number of arguments reaches the
    /// table's arity refusal like it does in the other two engines, instead of the engine's own
    /// indexer exception (too few) or a silently dropped argument (too many).
    /// </summary>
    private static object Answer(Context context, Arguments arguments, string name)
    {
        var values = new List<object?>(arguments.Length);
        foreach (var argument in arguments.AsEnumerable())
        {
            values.Add(argument);
        }

        var function = PresentationFunctionTable.For(SettingsOf(context))[name];
        return function(values) switch
        {
            PresentationResult.Text text => text.Value,
            PresentationResult.Flag flag => flag.Value,
            PresentationResult.Refused refused => throw new PresentationRefusedException(
                PresentationFailures.Describe(refused.Reason, name, recordOrdinal: null)),
            _ => string.Empty,
        };
    }

    /// <summary>The block's settings, read from the rendering model the composer built.</summary>
    private static PresentationSettings SettingsOf(Context context)
    {
        if (context.Value is IReadOnlyDictionary<string, object?> model
            && model.TryGetValue("render", out var member)
            && member is IReadOnlyDictionary<string, object?> render)
        {
            var culture = render.TryGetValue("culture", out var cultureName) ? cultureName as string : null;
            var timeZone = render.TryGetValue("timeZone", out var zoneName) ? zoneName as string : null;
            var clock = render.TryGetValue("clock", out var instant) && instant is string text
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
                    ? moment
                    : (DateTimeOffset?)null;
            return PresentationSettingsResolver.Resolve(new PresentationValues(culture, timeZone, clock));
        }

        throw new PresentationUnavailable("culture");
    }
}
