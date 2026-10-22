using System.Globalization;

namespace StreamingDataSpike.Rendering;

/// <summary>
/// Converts the typed record model into render values: numbers become their
/// invariant text (scale kept, for example <c>1000.00</c>), so that the three
/// engines print them identically whatever the current culture.
/// </summary>
internal static class TemplateValues
{
    /// <summary>A converted copy of an object.</summary>
    public static Dictionary<string, object?> FromModel(Dictionary<string, object?> values)
    {
        var result = new Dictionary<string, object?>(values.Count, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            result[key] = Convert(value);
        }

        return result;
    }

    /// <summary>The invariant text of a decimal.</summary>
    public static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static object? Convert(object? value) => value switch
    {
        decimal number => Text(number),
        bool flag => flag ? "true" : "false",
        Dictionary<string, object?> nested => FromModel(nested),
        List<object?> list => list.ConvertAll(Convert),
        _ => value,
    };
}
