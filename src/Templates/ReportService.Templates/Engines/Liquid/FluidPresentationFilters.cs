using Fluid;
using Fluid.Values;

namespace ReportService.Templates;

/// <summary>
/// The presentation table as Liquid sees it: one filter per name, added to the options a
/// part renders with. It is the only place Fluid's own filter API meets the table, and the
/// options are built for every render with the block's settings, so a prepared package
/// shared between blocks never presents another block's culture.
/// </summary>
public static class FluidPresentationFilters
{
    /// <summary>Registers the ten names on <paramref name="options"/>.</summary>
    /// <param name="options">The options the part renders with.</param>
    /// <param name="settings">The block's presentation settings.</param>
    public static void Register(TemplateOptions options, PresentationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        var table = PresentationFunctionTable.For(settings);

        foreach (var (name, function) in table)
        {
            var bound = function;
            options.Filters.AddFilter(name, (input, arguments, _) => new ValueTask<FluidValue>(Answer(bound, name, input, arguments)));
        }
    }

    private static FluidValue Answer(PresentationFunction function, string name, FluidValue input, FilterArguments arguments)
    {
        var values = new List<object?>(arguments.Count + 1) { ValueOf(input) };
        for (var index = 0; index < arguments.Count; index++)
        {
            values.Add(ValueOf(arguments.At(index)));
        }

        return function(values) switch
        {
            PresentationResult.Text text => new StringValue(text.Value),
            PresentationResult.Flag flag => BooleanValue.Create(flag.Value),
            PresentationResult.Refused refused => throw new PresentationRefusedException(
                PresentationFailures.Describe(refused.Reason, name, recordOrdinal: null)),
            _ => new StringValue(string.Empty),
        };
    }

    // The value as the render context holds it: text. A missing value stays missing, so a
    // comparison over it is the type mismatch the table reports and never an empty operand.
    private static object? ValueOf(FluidValue value) =>
        value.IsNil() ? null : value.ToStringValue();
}
