using System.Globalization;
using System.Text.Json;
using Scriban.Runtime;

namespace MarkdownSpike.Rendering;

/// <summary>
/// Converts JSON values to template values, keeping every property name
/// exactly as written in the data (no renaming).
/// </summary>
internal static class ScriptModel
{
    public static ScriptObject ToScriptObject(JsonElement element)
    {
        var result = new ScriptObject();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            result.SetValue(property.Name, ToValue(property.Value), false);
        }

        return result;
    }

    public static object? ToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ToScriptObject(element),
        JsonValueKind.Array => ToArray(element),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => ToNumber(element.GetRawText()),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>Integral numbers without a decimal point become long; others decimal, keeping their scale.</summary>
    public static object ToNumber(string raw)
    {
        if (raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0
            && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole))
        {
            return whole;
        }

        return decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static ScriptArray ToArray(JsonElement element)
    {
        var result = new ScriptArray();
        foreach (JsonElement item in element.EnumerateArray())
        {
            result.Add(ToValue(item));
        }

        return result;
    }
}
