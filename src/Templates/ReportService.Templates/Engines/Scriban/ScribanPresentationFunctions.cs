using Scriban;
using Scriban.Runtime;

namespace ReportService.Templates;

/// <summary>
/// The presentation table as Scriban sees it: one function per name, registered on the
/// scope a part renders with. It is the only place Scriban's own function API meets the
/// table, and it is called for every render with the block's settings, so a prepared
/// package shared between blocks never carries another block's culture, zone or clock.
/// </summary>
public static class ScribanPresentationFunctions
{
    /// <summary>Registers the nine names on <paramref name="globals"/>.</summary>
    /// <param name="globals">The scope the part renders with.</param>
    /// <param name="settings">The block's presentation settings.</param>
    public static void Register(ScriptObject globals, PresentationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(globals);
        ArgumentNullException.ThrowIfNull(settings);
        var table = PresentationFunctionTable.For(settings);

        globals[PresentationFunctionTable.FormatNumberName] = TwoArguments(table, PresentationFunctionTable.FormatNumberName);
        globals[PresentationFunctionTable.FormatDateName] = TwoArguments(table, PresentationFunctionTable.FormatDateName);
        globals[PresentationFunctionTable.NumGreaterThanName] = TwoArguments(table, PresentationFunctionTable.NumGreaterThanName);
        globals[PresentationFunctionTable.NumGreaterOrEqualName] = TwoArguments(table, PresentationFunctionTable.NumGreaterOrEqualName);
        globals[PresentationFunctionTable.NumLessThanName] = TwoArguments(table, PresentationFunctionTable.NumLessThanName);
        globals[PresentationFunctionTable.NumLessOrEqualName] = TwoArguments(table, PresentationFunctionTable.NumLessOrEqualName);
        globals[PresentationFunctionTable.NumEqualName] = TwoArguments(table, PresentationFunctionTable.NumEqualName);
        globals[PresentationFunctionTable.NumNotEqualName] = TwoArguments(table, PresentationFunctionTable.NumNotEqualName);
        globals[PresentationFunctionTable.NumSignName] = OneArgument(table, PresentationFunctionTable.NumSignName);
    }

    private static object TwoArguments(IReadOnlyDictionary<string, PresentationFunction> table, string name) =>
        DynamicCustomFunction.Create(new Func<object?, object?, object>(
            (left, right) => Answer(table[name], name, [left, right])));

    private static object OneArgument(IReadOnlyDictionary<string, PresentationFunction> table, string name) =>
        DynamicCustomFunction.Create(new Func<object?, object>(value => Answer(table[name], name, [value])));

    private static object Answer(PresentationFunction function, string name, IReadOnlyList<object?> arguments) =>
        function(arguments) switch
        {
            PresentationResult.Text text => text.Value,
            PresentationResult.Flag flag => flag.Value,
            PresentationResult.Refused refused => throw new PresentationRefusedException(
                PresentationFailures.Describe(refused.Reason, name, recordOrdinal: null)),
            _ => string.Empty,
        };
}
