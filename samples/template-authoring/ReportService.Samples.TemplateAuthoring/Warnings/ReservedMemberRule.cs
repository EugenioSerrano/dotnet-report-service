using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ReportService.Templates;

namespace ReportService.Samples.TemplateAuthoring;

/// <summary>
/// A template reading <c>x.size</c> where <c>x</c> is an object with a field <c>size</c>: the
/// engine answers the member count and the field is never printed. The rule reads each
/// template source's expressions (string literals blanked), finds member paths ending in a
/// shadowing member of the package's engine, and warns when the sample shows the shadow:
/// for a path that starts at <c>item</c>, <c>group.first</c> or <c>header</c>, when that object
/// carries the field in the sample; for a path it cannot resolve (a loop variable, an
/// assigned name), when any object of the sample carries a field of that name.
/// </summary>
public sealed partial class ReservedMemberRule : IWarningRule
{
    /// <summary>The stable name of the trap.</summary>
    public const string Code = "reserved-member-shadows-field";

    private static readonly string[] NoDataRoots = ["block", "render", "aggregates", "forloop"];

    /// <inheritdoc />
    public IEnumerable<AuthoringWarning> Find(WarningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var members = ReservedMembers.Of(input.Engine);
        if (members.Count == 0)
        {
            yield break;
        }

        foreach (var (file, text) in input.Sources)
        {
            var lineStarts = LineStarts(text);
            foreach (Match region in Expression().Matches(text))
            {
                var blanked = BlankStrings(region.Value);
                foreach (Match path in MemberPath().Matches(blanked))
                {
                    var segments = path.Value.Split('.');
                    for (var index = 1; index < segments.Length; index++)
                    {
                        var member = segments[index];
                        if (!members.Contains(member, StringComparer.Ordinal))
                        {
                            continue;
                        }

                        var prefix = string.Join('.', segments, 0, index);
                        if (Shadowed(prefix, member, input) is not { } field)
                        {
                            continue;
                        }

                        var offset = region.Index + path.Index + prefix.Length + 1;
                        var line = Array.BinarySearch(lineStarts, offset);
                        line = line >= 0 ? line : ~line - 1;
                        var column = offset - lineStarts[line] + 1;
                        yield return new AuthoringWarning(
                            Code,
                            new WarningOrder(0, file, line + 1, column),
                            [
                                new("file", file),
                                new("line", (line + 1).ToString(CultureInfo.InvariantCulture)),
                                new("column", column.ToString(CultureInfo.InvariantCulture)),
                                new("expression", prefix + "." + member),
                                new("field", field),
                            ],
                            "in Liquid ." + member + " is the member count; read " + prefix + "[\"" + member + "\"]");
                    }
                }
            }
        }
    }

    // The data path of a field the member shadows, or null when the sample shows no shadow.
    private static string? Shadowed(string prefix, string member, WarningInput input)
    {
        var segments = prefix.Split('.');
        var root = segments[0];
        if (NoDataRoots.Contains(root, StringComparer.Ordinal))
        {
            return null;
        }

        if (root == "header")
        {
            return Carries(Resolve(input.Header, segments.Skip(1)), member) ? prefix + "." + member : null;
        }

        if (root == "item" || (root == "group" && segments.Length > 1 && segments[1] == "first"))
        {
            var rest = root == "item" ? segments.Skip(1) : segments.Skip(2);
            var shown = root == "item" ? prefix : "item" + string.Concat(segments.Skip(2).Select(segment => "." + segment));
            return input.Records.Any(record => Carries(Resolve(record.Values, rest), member)) ? shown + "." + member : null;
        }

        if (root == "group")
        {
            return null;
        }

        // A name the template bound itself: any object of the sample that carries the field.
        var found = FirstCarrier(input.Header, "header", member);
        foreach (var record in input.Records)
        {
            found ??= FirstCarrier(record.Values, "item", member);
        }

        return found;
    }

    private static object? Resolve(object? value, IEnumerable<string> segments)
    {
        var current = value;
        foreach (var segment in segments)
        {
            if (current is not IReadOnlyDictionary<string, object?> map || !map.TryGetValue(segment, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static bool Carries(object? value, string member) =>
        value is IReadOnlyDictionary<string, object?> map && map.ContainsKey(member);

    private static string? FirstCarrier(object? value, string path, string member)
    {
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> map:
                if (map.ContainsKey(member))
                {
                    return path + "." + member;
                }

                foreach (var (name, child) in map)
                {
                    if (FirstCarrier(child, path + "." + name, member) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case string:
                return null;
            case IEnumerable items:
                var index = 0;
                foreach (var item in items)
                {
                    if (FirstCarrier(item, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", member) is { } found)
                    {
                        return found;
                    }

                    index++;
                }

                return null;
            default:
                return null;
        }
    }

    private static string BlankStrings(string expression)
    {
        var blanked = new StringBuilder(expression);
        var quote = '\0';
        for (var index = 0; index < blanked.Length; index++)
        {
            var character = blanked[index];
            if (quote == '\0')
            {
                if (character is '"' or '\'')
                {
                    quote = character;
                }
            }
            else if (character == quote)
            {
                quote = '\0';
            }
            else
            {
                blanked[index] = ' ';
            }
        }

        return blanked.ToString();
    }

    private static int[] LineStarts(string text)
    {
        List<int> starts = [0];
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        return [.. starts];
    }

    [GeneratedRegex(@"\{\{.*?\}\}|\{%.*?%\}", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Expression();

    [GeneratedRegex(@"(?<![\w.\]""'])[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MemberPath();
}
