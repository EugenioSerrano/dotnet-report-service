using System.Globalization;
using System.Text.RegularExpressions;
using ReportService.TemplatePackages.Manifest;

namespace ReportService.TemplateChecks.Scanning;

/// <summary>How a template file writes its outputs and tags.</summary>
public enum TemplateDialect
{
    /// <summary><c>{{ … }}</c> outputs and <c>include</c> calls.</summary>
    Scriban,

    /// <summary><c>{{ … }}</c> / <c>{{{ … }}}</c> mustaches and <c>{{&gt; partial}}</c> references.</summary>
    Handlebars,

    /// <summary><c>{{ … }}</c> outputs and <c>{% … %}</c> tags.</summary>
    Liquid,
}

/// <summary>One output of a template file, as the file's text writes it.</summary>
/// <param name="Offset">The character offset the output starts at.</param>
/// <param name="Line">Its 1-based line.</param>
/// <param name="Column">Its 1-based column.</param>
/// <param name="Expression">The expression's text, without the delimiters.</param>
/// <param name="IsData">Whether it reads a value derived from the records.</param>
/// <param name="IsEscaped">Whether the encoding the product applies covers it.</param>
/// <param name="IsRaw">Whether it is a raw output, which the lint refuses.</param>
public sealed record ScannedOutput(int Offset, int Line, int Column, string Expression, bool IsData, bool IsEscaped, bool IsRaw);

/// <summary>One include or partial reference, as the file writes it.</summary>
/// <param name="Offset">The character offset the reference starts at.</param>
/// <param name="Line">Its 1-based line.</param>
/// <param name="Column">Its 1-based column.</param>
/// <param name="Name">The name, or the expression's text when it is not a fixed name.</param>
/// <param name="IsLiteral">Whether the name is fixed text.</param>
public sealed record ScannedInclude(int Offset, int Line, int Column, string Name, bool IsLiteral);

/// <summary>One call of a name the lint judges.</summary>
/// <param name="Offset">The character offset it starts at.</param>
/// <param name="Line">Its 1-based line.</param>
/// <param name="Column">Its 1-based column.</param>
/// <param name="Name">The function, filter or tag name.</param>
public sealed record ScannedCall(int Offset, int Line, int Column, string Name);

/// <summary>One range a Liquid file writes.</summary>
/// <param name="Line">Its 1-based line.</param>
/// <param name="Column">Its 1-based column.</param>
/// <param name="From">The lower bound's text.</param>
/// <param name="To">The upper bound's text.</param>
/// <param name="FromIsInteger">Whether the lower bound is an integer literal.</param>
/// <param name="ToIsInteger">Whether the upper bound is an integer literal.</param>
/// <param name="Count">The number of values the range would materialise, when both bounds are literals.</param>
public sealed record ScannedRange(int Line, int Column, string From, string To, bool FromIsInteger, bool ToIsInteger, long? Count);

/// <summary>A span of the file that is template syntax, not document text.</summary>
/// <param name="Start">Its first character.</param>
/// <param name="Length">Its length.</param>
public sealed record TemplateRegion(int Start, int Length);

/// <summary>
/// What one template file's text says: its outputs, its includes, the calls the
/// lint judges and its ranges. The scan reads the source the engine's parser
/// accepted; for Handlebars it is the only reading there is (the library exposes
/// no syntax tree at 2.4.3), and for Scriban and Liquid it is applied to the same
/// text so the three engines are judged by one rule set.
/// </summary>
public static partial class TemplateSourceScan
{
    private static readonly string[] DataRoots = ["header", "item", "group", "aggregates"];

    private static readonly string[] ForbiddenNames =
        ["object.eval", "object.eval_template", "date.now", "log", "increment", "decrement", "cycle"];

    /// <summary>Scans one file.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="dialect">How the file writes its outputs.</param>
    public static SourceScanResult Scan(string text, TemplateDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(text);

        var outputs = new List<ScannedOutput>();
        var includes = new List<ScannedInclude>();
        var calls = new List<ScannedCall>();
        var ranges = new List<ScannedRange>();
        var regions = new List<TemplateRegion>();
        var assignedData = new HashSet<string>(StringComparer.Ordinal);
        var lineStarts = LineStarts(text);

        var index = 0;
        while (index < text.Length)
        {
            if (dialect == TemplateDialect.Handlebars && StartsWith(text, index, "{{{{"))
            {
                var close = text.IndexOf("{{{{/", index + 4, StringComparison.Ordinal);
                var end = close < 0 ? text.Length : text.IndexOf("}}}}", close, StringComparison.Ordinal);
                var length = end < 0 ? text.Length - index : end + 4 - index;
                regions.Add(new TemplateRegion(index, length));
                index += length;
                continue;
            }

            if (StartsWith(text, index, "{{"))
            {
                var raw = dialect == TemplateDialect.Handlebars && StartsWith(text, index, "{{{");
                var openLength = raw ? 3 : 2;
                var close = text.IndexOf(raw ? "}}}" : "}}", index + openLength, StringComparison.Ordinal);
                if (close < 0)
                {
                    break;
                }

                var end = close + (raw ? 3 : 2);
                var inner = text[(index + openLength)..close];
                regions.Add(new TemplateRegion(index, end - index));
                ScanMustache(inner, index, raw, dialect, lineStarts, assignedData, outputs, includes, calls, ranges);
                index = end;
                continue;
            }

            if (dialect == TemplateDialect.Liquid && StartsWith(text, index, "{%"))
            {
                var close = text.IndexOf("%}", index + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    break;
                }

                var inner = text[(index + 2)..close].Trim();
                regions.Add(new TemplateRegion(index, close + 2 - index));

                if (inner.StartsWith("raw", StringComparison.Ordinal))
                {
                    var rawClose = text.IndexOf("{% endraw %}", close + 2, StringComparison.Ordinal);
                    var rawEnd = rawClose < 0 ? text.Length : rawClose + "{% endraw %}".Length;
                    if (rawClose > 0)
                    {
                        var body = text[(close + 2)..rawClose];
                        if (body.Contains("{{", StringComparison.Ordinal))
                        {
                            var position = Position(lineStarts, index);
                            outputs.Add(new ScannedOutput(index, position.Line, position.Column, body, false, false, true));
                        }
                    }

                    regions.Add(new TemplateRegion(close + 2, rawEnd - (close + 2)));
                    index = rawEnd;
                    continue;
                }

                ScanLiquidTag(inner, index, lineStarts, assignedData, includes, calls, ranges);
                index = close + 2;
                continue;
            }

            index++;
        }

        return new SourceScanResult(outputs, includes, calls, ranges, regions);
    }

    private static void ScanMustache(
        string inner,
        int offset,
        bool raw,
        TemplateDialect dialect,
        IReadOnlyList<int> lineStarts,
        HashSet<string> assignedData,
        List<ScannedOutput> outputs,
        List<ScannedInclude> includes,
        List<ScannedCall> calls,
        List<ScannedRange> ranges)
    {
        var position = Position(lineStarts, offset);
        var text = inner.Trim();

        if (dialect == TemplateDialect.Handlebars)
        {
            if (text.StartsWith('!') || text.StartsWith("!--", StringComparison.Ordinal))
            {
                return;
            }

            if (text.StartsWith('>'))
            {
                var name = text[1..].Trim();
                var literal = !name.StartsWith('(');
                includes.Add(new ScannedInclude(offset, position.Line, position.Column, name, literal));
                return;
            }

            if (text.StartsWith('#') || text.StartsWith('/'))
            {
                if (text.StartsWith('#') && text.Length > 1 && char.IsLetter(text[1]))
                {
                    calls.Add(new ScannedCall(offset, position.Line, position.Column, "block"));
                }

                return;
            }

            if (text.StartsWith('&'))
            {
                raw = true;
                text = text[1..].Trim();
            }

            if (text.StartsWith("else", StringComparison.Ordinal))
            {
                return;
            }

            foreach (var call in CallsIn(text))
            {
                if (ForbiddenNames.Contains(call, StringComparer.Ordinal))
                {
                    calls.Add(new ScannedCall(offset, position.Line, position.Column, call));
                }
            }

            // A helper is called as a bare word followed by its arguments, so the
            // first word of the mustache is a call too when it names one.
            var head = FirstWord(text);
            if (ForbiddenNames.Contains(head, StringComparer.Ordinal))
            {
                calls.Add(new ScannedCall(offset, position.Line, position.Column, head));
            }

            outputs.Add(new ScannedOutput(offset, position.Line, position.Column, text, IsData(text, assignedData), !raw, raw));
            return;
        }

        if (dialect == TemplateDialect.Scriban)
        {
            if (text.StartsWith("include ", StringComparison.Ordinal))
            {
                var argument = text[8..].Trim();
                var literal = argument.Length >= 2 && argument[0] == '"' && argument[^1] == '"';
                var name = literal ? argument[1..^1] : argument;
                includes.Add(new ScannedInclude(offset, position.Line, position.Column, name, literal));
                return;
            }

            if (Assignment().Match(text) is { Success: true } assignment)
            {
                if (IsData(assignment.Groups[2].Value, assignedData))
                {
                    _ = assignedData.Add(assignment.Groups[1].Value);
                }

                return;
            }

            foreach (var call in ForbiddenCalls(text))
            {
                calls.Add(new ScannedCall(offset, position.Line, position.Column, call));
            }

            if (IsScribanControlFlow(text, assignedData))
            {
                return;
            }

            var escaped = EndsWithHtmlEscape(text);
            outputs.Add(new ScannedOutput(offset, position.Line, position.Column, text, IsData(text, assignedData), escaped, false));
            return;
        }

        // Liquid output.
        foreach (var call in CallsIn(text))
        {
            if (ForbiddenNames.Contains(call, StringComparer.Ordinal))
            {
                calls.Add(new ScannedCall(offset, position.Line, position.Column, call));
            }
        }

        foreach (var range in RangesIn(text))
        {
            ranges.Add(ToRange(range, position.Line, position.Column));
        }

        var rawFilter = text.Split('|').Skip(1).Any(stage => stage.Trim().StartsWith("raw", StringComparison.Ordinal));
        outputs.Add(new ScannedOutput(offset, position.Line, position.Column, text, IsData(text, assignedData), !rawFilter, rawFilter));
    }

    private static void ScanLiquidTag(
        string inner,
        int offset,
        IReadOnlyList<int> lineStarts,
        HashSet<string> assignedData,
        List<ScannedInclude> includes,
        List<ScannedCall> calls,
        List<ScannedRange> ranges)
    {
        var position = Position(lineStarts, offset);
        var text = inner.Trim();

        if (text.StartsWith("include ", StringComparison.Ordinal))
        {
            var argument = text[8..].Trim();
            var literal = argument.Length >= 2 && (argument[0] == '"' || argument[0] == '\'');
            var name = literal ? argument[1..^1] : argument;
            includes.Add(new ScannedInclude(offset, position.Line, position.Column, name, literal));
            return;
        }

        if (text.StartsWith("render ", StringComparison.Ordinal))
        {
            var argument = text[7..].Trim();
            var literal = argument.Length >= 2 && (argument[0] == '"' || argument[0] == '\'');
            var name = literal ? argument[1..^1] : argument;
            includes.Add(new ScannedInclude(offset, position.Line, position.Column, name, literal));
            return;
        }

        if (text.StartsWith("assign ", StringComparison.Ordinal))
        {
            var equals = text.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                var name = text[7..equals].Trim();
                if (IsData(text[(equals + 1)..], assignedData))
                {
                    _ = assignedData.Add(name);
                }
            }

            return;
        }

        var tag = FirstWord(text);
        if (ForbiddenNames.Contains(tag, StringComparer.Ordinal))
        {
            calls.Add(new ScannedCall(offset, position.Line, position.Column, tag));
        }

        if (tag is "for" or "tablerow" or "when" or "case")
        {
            foreach (var range in RangesIn(text))
            {
                ranges.Add(ToRange(range, position.Line, position.Column));
            }
        }
    }

    private static ScannedRange ToRange(Match match, int line, int column)
    {
        var from = match.Groups[1].Value.Trim();
        var to = match.Groups[2].Value.Trim();
        var fromInteger = IntegerLiteral().IsMatch(from);
        var toInteger = IntegerLiteral().IsMatch(to);
        long? count = null;
        if (fromInteger && toInteger
            && long.TryParse(from, NumberStyles.None, CultureInfo.InvariantCulture, out var low)
            && long.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out var high))
        {
            count = high - low + 1;
        }

        return new ScannedRange(line, column, from, to, fromInteger, toInteger, count);
    }

    private static IReadOnlyList<string> CallsIn(string expression)
    {
        var names = new List<string>();
        foreach (Match match in CallName().Matches(expression))
        {
            names.Add(match.Groups[1].Value);
        }

        return names;
    }

    private static IReadOnlyList<string> ForbiddenCalls(string expression)
    {
        var names = new List<string>();
        foreach (var forbidden in ForbiddenNames)
        {
            if (expression.Contains(forbidden, StringComparison.Ordinal))
            {
                names.Add(forbidden);
            }
        }

        return names;
    }

    /// <summary>Whether an expression reads a value derived from the records.</summary>
    /// <param name="expression">The output's or assignment's expression text.</param>
    /// <param name="assignedData">The variables already assigned from data in this file.</param>
    public static bool IsData(string expression, IReadOnlySet<string> assignedData)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(assignedData);

        foreach (Match match in Identifier().Matches(expression))
        {
            var name = match.Groups[1].Value;
            if (DataRoots.Contains(name, StringComparer.Ordinal) || assignedData.Contains(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a Scriban output ends with <c>html.escape</c>.</summary>
    /// <param name="expression">The output's expression text.</param>
    public static bool EndsWithHtmlEscape(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var trimmed = expression.Trim();
        if (trimmed.StartsWith("html.escape(", StringComparison.Ordinal))
        {
            return true;
        }

        var stages = trimmed.Split('|');
        return stages.Length > 1 && stages[^1].Trim().StartsWith("html.escape", StringComparison.Ordinal);
    }

    private static IReadOnlyList<Match> RangesIn(string expression)
    {
        var matches = new List<Match>();
        foreach (Match match in Range().Matches(expression))
        {
            matches.Add(match);
        }

        return matches;
    }

    private static (int Line, int Column) Position(IReadOnlyList<int> lineStarts, int offset)
    {
        var line = 1;
        for (var i = 1; i < lineStarts.Count; i++)
        {
            if (lineStarts[i] <= offset)
            {
                line = i + 1;
            }
            else
            {
                break;
            }
        }

        return (line, offset - lineStarts[line - 1] + 1);
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        return starts;
    }

    private static string FirstWord(string text)
    {
        var end = 0;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '-'))
        {
            end++;
        }

        return text[..end];
    }

    /// <summary>
    /// Whether a Scriban code block is a single <c>if</c>, <c>else if</c>, <c>else</c>,
    /// <c>for</c> or <c>end</c> statement, which writes nothing. A loop over a value
    /// derived from the records makes its variable one too, so its outputs stay checked.
    /// A block holding more than one statement is never read as one.
    /// </summary>
    private static bool IsScribanControlFlow(string text, HashSet<string> assignedData)
    {
        if (text.Contains(';', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal))
        {
            return false;
        }

        if (text is "end" or "else" || ScribanCondition().IsMatch(text))
        {
            return true;
        }

        if (ScribanLoop().Match(text) is { Success: true } loop)
        {
            if (IsData(loop.Groups[2].Value, assignedData))
            {
                _ = assignedData.Add(loop.Groups[1].Value);
            }

            return true;
        }

        return false;
    }

    private static bool StartsWith(string text, int index, string value) =>
        index + value.Length <= text.Length
        && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    [GeneratedRegex("^\\s*([A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Assignment();

    [GeneratedRegex("^(?:if|else\\s+if)\\s+\\S", RegexOptions.CultureInvariant)]
    private static partial Regex ScribanCondition();

    [GeneratedRegex("^for\\s+([A-Za-z_][A-Za-z0-9_]*)\\s+in\\s+(\\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScribanLoop();

    [GeneratedRegex("^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerLiteral();

    [GeneratedRegex("(?<![A-Za-z0-9_.])([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex("(?<![A-Za-z0-9_.])([A-Za-z_][A-Za-z0-9_.]*)\\s*\\(", RegexOptions.CultureInvariant)]
    private static partial Regex CallName();

    [GeneratedRegex("\\(([^()]*)\\.\\.([^()]*)\\)", RegexOptions.CultureInvariant)]
    private static partial Regex Range();
}

/// <summary>What one file's scan found.</summary>
/// <param name="Outputs">Its outputs.</param>
/// <param name="Includes">Its include and partial references.</param>
/// <param name="Calls">The calls its lint judges.</param>
/// <param name="Ranges">The ranges it writes.</param>
/// <param name="Regions">The spans that are template syntax rather than document text.</param>
public sealed record SourceScanResult(
    IReadOnlyList<ScannedOutput> Outputs,
    IReadOnlyList<ScannedInclude> Includes,
    IReadOnlyList<ScannedCall> Calls,
    IReadOnlyList<ScannedRange> Ranges,
    IReadOnlyList<TemplateRegion> Regions);
