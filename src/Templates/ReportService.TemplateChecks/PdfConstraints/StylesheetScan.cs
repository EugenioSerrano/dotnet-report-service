using System.Text;

namespace ReportService.TemplateChecks.PdfConstraints;

/// <summary>
/// Reads a package's own stylesheet text once and yields one record per
/// <c>property: value</c> declaration: the file, the line, the rule's selector and
/// its subject, the origin the declaration sits in and its position there, its
/// importance, the at-rule above it and the two values. Comments and strings are
/// read as text, so a <c>;</c> or a <c>}</c> inside them never ends a declaration;
/// an at-rule's block is entered rather than adopted as a selector, and a
/// declaration written directly inside it is recorded with no selector. A text the
/// walk cannot finish is read as far as it goes and never throws: a declaration the
/// walk never saw reaches nothing.
/// </summary>
public static class StylesheetScan
{
    /// <summary>The declarations of one stylesheet's text.</summary>
    /// <param name="file">The package-relative path the text was read from.</param>
    /// <param name="text">The decoded text.</param>
    /// <param name="source">Where the text came from.</param>
    /// <param name="origin">The position of this text in the author origin, compared left to right.</param>
    public static IReadOnlyList<StyleDeclaration> Read(
        string file,
        string text,
        DeclarationSource source,
        IReadOnlyList<int> origin)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(origin);

        return Walk(file, text, source, origin);
    }

    /// <summary>
    /// The declarations of one element's style attribute: the element is their
    /// subject, and they all sit on the attribute's line.
    /// </summary>
    /// <param name="file">The package-relative path of the part that carries the element.</param>
    /// <param name="line">The 1-based line the attribute is written on.</param>
    /// <param name="subject">The element that carries the attribute.</param>
    /// <param name="text">The attribute's value.</param>
    /// <param name="origin">The element's position in the author origin, compared left to right.</param>
    public static IReadOnlyList<StyleDeclaration> ReadInline(
        string file,
        int line,
        string subject,
        string text,
        IReadOnlyList<int> origin)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(line);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(origin);

        var declarations = new List<StyleDeclaration>();
        foreach (var piece in Pieces(StripComments(text)))
        {
            var declaration = Declaration(file, line, string.Empty, subject, DeclarationSource.Inline, origin, declarations.Count + 1, piece);
            if (declaration is not null)
            {
                declarations.Add(declaration);
            }
        }

        return declarations;
    }

    /// <summary>The members of a selector list: each is a selector of its own, as CSS reads it.</summary>
    /// <param name="selector">The selector a rule carries, whole.</param>
    public static IEnumerable<string> Selectors(string selector)
    {
        var start = 0;
        var depth = 0;
        for (var index = 0; index < selector.Length; index++)
        {
            var character = selector[index];
            if (character is '[' or '(')
            {
                depth++;
            }
            else if (character is ']' or ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (character == ',' && depth == 0)
            {
                var member = selector[start..index].Trim();
                if (member.Length > 0)
                {
                    yield return member;
                }

                start = index + 1;
            }
        }

        var last = selector[start..].Trim();
        if (last.Length > 0)
        {
            yield return last;
        }
    }

    /// <summary>The style elements of a part, in document order, with the line their content starts on.</summary>
    /// <param name="text">The part's decoded text.</param>
    public static IEnumerable<StyleBlock> Blocks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var index = 0;
        while (index < text.Length)
        {
            if (Starts(text, index, "<!--"))
            {
                index = Past(text, index + 4, "-->");
                continue;
            }

            if (!Starts(text, index, "<style", ignoreCase: true)
                || (index + "<style".Length < text.Length
                    && !char.IsWhiteSpace(text[index + "<style".Length])
                    && text[index + "<style".Length] != '>'))
            {
                index++;
                continue;
            }

            var open = text.IndexOf('>', index);
            if (open < 0)
            {
                yield break;
            }

            var contentStart = open + 1;
            var close = IndexOf(text, contentStart, "</style", ignoreCase: true);
            var contentEnd = close < 0 ? text.Length : close;
            yield return new StyleBlock(text[contentStart..contentEnd], LineAt(text, contentStart));
            index = close < 0 ? text.Length : Past(text, close, ">");
        }
    }

    private static bool Starts(string text, int index, string value, bool ignoreCase = false) =>
        index + value.Length <= text.Length
        && string.Compare(text, index, value, 0, value.Length, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) == 0;

    private static int Past(string text, int index, string terminator)
    {
        var found = text.IndexOf(terminator, index, StringComparison.Ordinal);
        return found < 0 ? text.Length : found + terminator.Length;
    }

    private static int IndexOf(string text, int start, string value, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var index = start;
        while (index + value.Length <= text.Length)
        {
            if (string.Compare(text, index, value, 0, value.Length, comparison) == 0)
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private static int LineAt(string text, int index)
    {
        var line = 1;
        for (var scan = 0; scan < index && scan < text.Length; scan++)
        {
            if (text[scan] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>The rightmost compound of a selector: the one the engine matches last, in a list too.</summary>
    private static string SubjectOf(string selector)
    {
        if (selector.Length == 0)
        {
            return string.Empty;
        }

        var compounds = selector.Split(
            [' ', '\t', '\n', '\r', '>', '+', '~', ','],
            StringSplitOptions.RemoveEmptyEntries);
        return compounds.Length == 0 ? string.Empty : compounds[^1];
    }

    private static IReadOnlyList<StyleDeclaration> Walk(
        string file,
        string text,
        DeclarationSource source,
        IReadOnlyList<int> origin)
    {
        var declarations = new List<StyleDeclaration>();
        var scopes = new List<(string Selector, DeclarationCondition Condition)>();
        var segment = new StringBuilder();
        var line = 1;
        var segmentLine = 1;
        var segmentHasText = false;
        var clean = StripComments(text);

        for (var index = 0; index < clean.Length; index++)
        {
            var character = clean[index];

            if (character is '"' or '\'')
            {
                var quote = character;
                _ = segment.Append(character);
                index++;
                while (index < clean.Length)
                {
                    var inner = clean[index];
                    _ = segment.Append(inner);
                    if (inner == '\n')
                    {
                        line++;
                    }

                    if (inner == '\\' && index + 1 < clean.Length)
                    {
                        index++;
                        _ = segment.Append(clean[index]);
                        if (clean[index] == '\n')
                        {
                            line++;
                        }
                    }
                    else if (inner == quote)
                    {
                        break;
                    }

                    index++;
                }

                continue;
            }

            if (character == '{'
                && index + 1 < clean.Length
                && clean[index + 1] is '{' or '%' or '#')
            {
                // A template expression is author text the engine evaluates, not a block:
                // it is carried whole so a selector or a value holding one stays one token.
                var closer = clean[index + 1] switch
                {
                    '{' => "}}",
                    '%' => "%}",
                    _ => "#}",
                };
                var end = clean.IndexOf(closer, index + 2, StringComparison.Ordinal);
                var stop = end < 0 ? clean.Length : end + closer.Length;
                if (!segmentHasText)
                {
                    segmentLine = line;
                    segmentHasText = true;
                }

                for (var scan = index; scan < stop; scan++)
                {
                    if (clean[scan] == '\n')
                    {
                        line++;
                    }
                }

                _ = segment.Append(clean, index, stop - index);
                index = stop - 1;
                continue;
            }

            if (character is '{')
            {
                var prelude = segment.ToString().Trim();
                segment.Clear();
                segmentHasText = false;
                var condition = scopes.Count > 0 ? scopes[^1].Condition : DeclarationCondition.None;
                scopes.Add(prelude.StartsWith('@')
                    ? (string.Empty, Condition(prelude, condition))
                    : (prelude, condition));
                continue;
            }

            if (character is '}' or ';')
            {
                var selector = scopes.Count > 0 ? scopes[^1].Selector : string.Empty;
                var conditionOfScope = scopes.Count > 0 ? scopes[^1].Condition : DeclarationCondition.None;
                var piece = segment.ToString();
                segment.Clear();
                segmentHasText = false;

                var declaration = Declaration(file, segmentLine, selector, SubjectOf(selector), source, origin, declarations.Count + 1, piece);
                if (declaration is not null)
                {
                    declarations.Add(declaration with { Condition = conditionOfScope });
                }

                if (character == '}' && scopes.Count > 0)
                {
                    scopes.RemoveAt(scopes.Count - 1);
                }

                continue;
            }

            _ = segment.Append(character);
            if (character == '\n')
            {
                line++;
            }
            else if (!char.IsWhiteSpace(character) && !segmentHasText)
            {
                segmentLine = line;
                segmentHasText = true;
            }
        }

        var last = Declaration(
            file,
            segmentLine,
            scopes.Count > 0 ? scopes[^1].Selector : string.Empty,
            SubjectOf(scopes.Count > 0 ? scopes[^1].Selector : string.Empty),
            source,
            origin,
            declarations.Count + 1,
            segment.ToString());
        if (last is not null)
        {
            declarations.Add(last with
            {
                Condition = scopes.Count > 0 ? scopes[^1].Condition : DeclarationCondition.None,
            });
        }

        return declarations;
    }

    private static StyleDeclaration? Declaration(
        string file,
        int line,
        string selector,
        string subject,
        DeclarationSource source,
        IReadOnlyList<int> origin,
        int position,
        string text)
    {
        var raw = text.Trim();
        if (raw.Length == 0 || raw.StartsWith('@'))
        {
            return null;
        }

        var colon = raw.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return null;
        }

        var property = raw[..colon].Trim().ToLowerInvariant();
        var value = raw[(colon + 1)..].Trim();
        if (property.Length == 0 || value.Length == 0)
        {
            return null;
        }

        var importance = DeclarationImportance.Normal;
        if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^"!important".Length].TrimEnd();
            importance = DeclarationImportance.Important;
        }

        return new StyleDeclaration(
            file,
            line,
            selector,
            subject,
            source,
            [.. origin, position],
            importance,
            DeclarationCondition.None,
            property,
            value);
    }

    private static DeclarationCondition Condition(string prelude, DeclarationCondition inherited)
    {
        var text = prelude.ToLowerInvariant();
        if (text.StartsWith("@media", StringComparison.Ordinal))
        {
            var query = text["@media".Length..].Trim();
            if (query.Contains('(', StringComparison.Ordinal))
            {
                return DeclarationCondition.Feature;
            }

            var type = query.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return type switch
            {
                "print" => DeclarationCondition.Print,
                "all" => DeclarationCondition.All,
                "screen" => DeclarationCondition.Screen,
                null or "" => inherited,
                _ => DeclarationCondition.OtherType,
            };
        }

        if (text.StartsWith("@supports", StringComparison.Ordinal))
        {
            return DeclarationCondition.Supports;
        }

        if (text.StartsWith("@container", StringComparison.Ordinal))
        {
            return DeclarationCondition.Container;
        }

        return text.StartsWith("@layer", StringComparison.Ordinal)
            ? DeclarationCondition.Layer
            : inherited;
    }

    private static IEnumerable<string> Pieces(string text)
    {
        var piece = new StringBuilder();
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character is '"' or '\'')
            {
                var quote = character;
                _ = piece.Append(character);
                index++;
                while (index < text.Length)
                {
                    var inner = text[index];
                    _ = piece.Append(inner);
                    if (inner == '\\' && index + 1 < text.Length)
                    {
                        index++;
                        _ = piece.Append(text[index]);
                    }
                    else if (inner == quote)
                    {
                        break;
                    }

                    index++;
                }

                continue;
            }

            if (character is ';')
            {
                yield return piece.ToString();
                _ = piece.Clear();
                continue;
            }

            _ = piece.Append(character);
        }

        yield return piece.ToString();
    }

    private static string StripComments(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                index += 2;
                while (index < text.Length)
                {
                    if (text[index] == '*' && index + 1 < text.Length && text[index + 1] == '/')
                    {
                        index++;
                        break;
                    }

                    _ = builder.Append(text[index] == '\n' ? '\n' : ' ');
                    index++;
                }

                continue;
            }

            _ = builder.Append(text[index]);
        }

        return builder.ToString();
    }
}
