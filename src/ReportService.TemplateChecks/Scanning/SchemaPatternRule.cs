using System.Text.Json;

namespace ReportService.TemplateChecks.Scanning;

/// <summary>
/// The take-only nested-repetition rule: a pattern that repeats a part which
/// itself repeats without limit is refused before the schema is compiled. It is a
/// best effort — it does not detect every catastrophic pattern, and the evaluator's
/// per-match timeout and the sample deadline are the backstops — but every shape it
/// refuses is one a match can take too long on.
/// </summary>
public static class SchemaPatternRule
{
    /// <summary>One pattern the rule refuses, with where it sits in the schema.</summary>
    /// <param name="Pointer">The JSON pointer of the pattern.</param>
    /// <param name="Pattern">The pattern the rule refused.</param>
    public sealed record Finding(string Pointer, string Pattern);

    /// <summary>Finds the first pattern the rule refuses, or null when none is.</summary>
    /// <param name="schema">The schema document's bytes.</param>
    public static Finding? Find(ReadOnlyMemory<byte> schema)
    {
        if (schema.IsEmpty)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(schema);
            return Walk(document.RootElement, "");
        }
        catch (JsonException)
        {
            // A document that does not parse is the profile's refusal, not this rule's.
            return null;
        }
    }

    /// <summary>Whether a regular expression repeats a part that repeats without limit.</summary>
    /// <param name="pattern">The pattern, in ECMA-262 syntax.</param>
    public static bool IsCatastrophic(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var unbounded = new Stack<bool>();
        unbounded.Push(false);
        var index = 0;
        while (index < pattern.Length)
        {
            var character = pattern[index];
            if (character == '\\')
            {
                index += 2;
                continue;
            }

            if (character == '[')
            {
                index = SkipClass(pattern, index);
                continue;
            }

            if (character == '(')
            {
                unbounded.Push(false);
                index++;
                continue;
            }

            if (character == ')')
            {
                var inner = unbounded.Pop();
                index++;
                var (upper, isUnbounded) = ReadQuantifier(pattern, ref index);
                if (inner && upper > 1)
                {
                    return true;
                }

                if (isUnbounded || upper > 1)
                {
                    var parent = unbounded.Pop();
                    unbounded.Push(parent || isUnbounded);
                }

                continue;
            }

            if (character is '*' or '+')
            {
                MarkUnbounded(unbounded);
                index++;
                if (index < pattern.Length && pattern[index] == '?')
                {
                    index++;
                }

                continue;
            }

            if (character == '{')
            {
                var start = index;
                var (upper, isUnbounded) = ReadQuantifier(pattern, ref index);
                if (isUnbounded)
                {
                    MarkUnbounded(unbounded);
                }

                if (index == start)
                {
                    index++;
                }

                continue;
            }

            index++;
        }

        return false;
    }

    private static void MarkUnbounded(Stack<bool> frames)
    {
        _ = frames.Pop();
        frames.Push(true);
    }

    private static (long Upper, bool Unbounded) ReadQuantifier(string pattern, ref int index)
    {
        if (index >= pattern.Length)
        {
            return (1, false);
        }

        if (pattern[index] is '*' or '+')
        {
            index++;
            if (index < pattern.Length && pattern[index] == '?')
            {
                index++;
            }

            return (long.MaxValue, true);
        }

        if (pattern[index] != '{')
        {
            return (1, false);
        }

        var close = pattern.IndexOf('}', index);
        if (close < 0)
        {
            return (1, false);
        }

        var body = pattern[(index + 1)..close];
        index = close + 1;
        var comma = body.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return (Parse(body), false);
        }

        var upperText = body[(comma + 1)..];
        if (upperText.Length == 0)
        {
            return (long.MaxValue, true);
        }

        return (Parse(upperText), false);
    }

    private static long Parse(string text) =>
        long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 1;

    private static int SkipClass(string pattern, int index)
    {
        index++;
        if (index < pattern.Length && pattern[index] == '^')
        {
            index++;
        }

        if (index < pattern.Length && pattern[index] == ']')
        {
            index++;
        }

        while (index < pattern.Length && pattern[index] != ']')
        {
            index += pattern[index] == '\\' ? 2 : 1;
        }

        return Math.Min(index + 1, pattern.Length);
    }

    private static Finding? Walk(JsonElement node, string pointer)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var member in node.EnumerateObject())
                {
                    var child = pointer + "/" + member.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                    if (member.Name == "pattern" && member.Value.ValueKind == JsonValueKind.String)
                    {
                        var pattern = member.Value.GetString()!;
                        if (IsCatastrophic(pattern))
                        {
                            return new Finding(child, pattern);
                        }
                    }

                    if (member.Name == "patternProperties" && member.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in member.Value.EnumerateObject())
                        {
                            if (IsCatastrophic(property.Name))
                            {
                                return new Finding(child + "/" + property.Name, property.Name);
                            }
                        }
                    }

                    if (Walk(member.Value, child) is { } finding)
                    {
                        return finding;
                    }
                }

                return null;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in node.EnumerateArray())
                {
                    if (Walk(item, pointer + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } finding)
                    {
                        return finding;
                    }

                    index++;
                }

                return null;
            default:
                return null;
        }
    }
}
