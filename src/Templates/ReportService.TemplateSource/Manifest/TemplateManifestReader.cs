using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace ReportService.TemplateSource.Manifest;

/// <summary>
/// The only type that reads <c>template.yaml</c>, and the only product type that
/// knows YAML. It reads the document through the low-level event parser — one
/// event at a time, so nothing past the first violation is ever parsed — and maps
/// it to the canonical JSON every later reader uses. The subset is narrow on
/// purpose: one document whose root is a mapping, plain and quoted and block
/// scalars, block and flow collections and comments; anchors, aliases, tags,
/// directives, extra documents, keys that are not scalars, duplicate keys and the
/// merge key are refused, and the input is bounded in size, depth and node count
/// before it can cost anything. Every refusal is the same answer — the file, the
/// line, the column and the rule — and the library's own message never becomes
/// the reason.
/// </summary>
public sealed partial class TemplateManifestReader
{
    /// <summary>The name the manifest has in every package, as a refusal names it.</summary>
    public const string FileName = PackagePath.ManifestFileName;

    /// <summary>The largest manifest read, in bytes.</summary>
    public const int MaxBytes = 65_536;

    /// <summary>The deepest nesting read, the root mapping counting as one level.</summary>
    public const int MaxDepth = 32;

    /// <summary>The most nodes read: scalars, keys and collections together.</summary>
    public const int MaxNodes = 10_000;

    private const string NotDocument = "the file is not valid YAML in the form this service reads";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Reads one manifest.</summary>
    /// <param name="stream">The bytes of <c>template.yaml</c>.</param>
    /// <returns>The document, or the refusal an author has to act on.</returns>
    public ManifestReadResult Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (ReadAtMost(stream, out var bytes) is { } readFailure)
        {
            return ManifestReadResult.Refused(readFailure);
        }

        if (Decode(bytes, out var text) is { } decodeFailure)
        {
            return ManifestReadResult.Refused(decodeFailure);
        }

        if (!ObeysCharacterRule(text, out var textFailure))
        {
            return ManifestReadResult.Refused(textFailure);
        }

        JsonObject document;
        try
        {
            document = Parse(text);
        }
        catch (YamlException exception)
        {
            return ManifestReadResult.Refused(Refusal(
                exception.Start,
                text,
                NotDocument));
        }

        var json = TemplateManifestJson.Write(document);
        return ManifestReadResult.Read(json, Typed(json));
    }

    private static TemplateManifest? Typed(string json)
    {
        try
        {
            return TemplateManifestJson.ReadTyped(json);
        }
        catch (JsonException)
        {
            // A member this build reads has a type it cannot take, or the engine is
            // one this deployment does not render: the take decides which rule that is.
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Reads the whole stream, refusing a manifest over the size limit.</summary>
    private static string? ReadAtMost(Stream stream, out byte[] bytes)
    {
        var buffer = new byte[MaxBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            int read;
            try
            {
                read = stream.Read(buffer, total, buffer.Length - total);
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
            {
                bytes = [];
                return $"{FileName}: the file could not be read";
            }

            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        if (total > MaxBytes)
        {
            bytes = [];
            var crossedAt = Encoding.UTF8.GetString(buffer, 0, MaxBytes);
            return RefusalAt(Position(crossedAt, crossedAt.Length), $"the manifest is larger than {MaxBytes.ToString(CultureInfo.InvariantCulture)} bytes");
        }

        bytes = buffer[..total];
        return null;
    }

    /// <summary>Decodes the bytes as strict UTF-8, removing one leading byte order mark.</summary>
    private static string? Decode(byte[] bytes, out string text)
    {
        text = string.Empty;

        // UTF-16 and UTF-32 are refused before decoding: a manifest is UTF-8.
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] or [0x00, 0x00, 0xFE, 0xFF, ..])
        {
            return $"{FileName} line 1, column 1: the manifest is not UTF-8 (a UTF-16 or UTF-32 byte order mark was found)";
        }

        var start = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        try
        {
            text = StrictUtf8.GetString(bytes, start, bytes.Length - start);
            return null;
        }
        catch (DecoderFallbackException)
        {
            return $"{FileName} line 1, column 1: the manifest is not valid UTF-8";
        }
    }

    /// <summary>The characters a manifest may hold, at the byte order mark and the line breaks alike.</summary>
    private static bool ObeysCharacterRule(string text, out string reason)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character is '\t' or '\n' or '\r')
            {
                continue;
            }

            if (character < '\u0020'
                || character == '\u007F'
                || character is >= '\u0080' and <= '\u009F'
                || character is '\uFEFF' or '\uFFFE' or '\uFFFF')
            {
                reason = RefusalAt(Position(text, index), "the text holds a character a manifest may not contain");
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Maps one document to the JSON every later reader uses.</summary>
    private static JsonObject Parse(string text)
    {
        var parser = new Parser(new StringReader(text));
        var state = new State();

        Move(parser);
        if (parser.Current is not StreamStart)
        {
            throw Violation(parser.Current!.Start, NotDocument);
        }

        Move(parser);
        if (parser.Current is StreamEnd)
        {
            throw Violation(parser.Current.Start, "the manifest is empty");
        }

        if (parser.Current is DocumentStart start)
        {
            RefuseDirectives(start);
            Move(parser);
        }

        if (parser.Current is DocumentStart)
        {
            throw Violation(parser.Current.Start, "a manifest holds exactly one document");
        }

        var root = ReadNode(parser, state, text);
        if (root is not JsonObject mapping)
        {
            throw Violation(parser.Current?.Start ?? default, "the manifest is not a mapping");
        }

        // One document, closed: anything but the end of the stream is a second one.
        Move(parser);
        if (parser.Current is DocumentEnd)
        {
            Move(parser);
        }

        if (parser.Current is not StreamEnd)
        {
            throw Violation(
                parser.Current?.Start ?? default,
                parser.Current is DocumentStart ? "a manifest holds exactly one document" : NotDocument);
        }

        return mapping;
    }

    private static JsonNode? ReadNode(Parser parser, State state, string text)
    {
        var current = parser.Current ?? throw Violation(default, NotDocument);
        switch (current)
        {
            case AnchorAlias alias:
                throw Violation(alias.Start, "anchors and aliases are not allowed; write the value where it is used");

            case Scalar scalar:
                Count(state, scalar);
                RefuseNode(scalar);
                return Resolve(scalar, text);

            case MappingStart start:
                Count(state, start);
                RefuseNode(start);
                return ReadMapping(parser, state, text, start);

            case SequenceStart start:
                Count(state, start);
                RefuseNode(start);
                return ReadSequence(parser, state, text, start);

            default:
                throw Violation(current.Start, NotDocument);
        }
    }

    private static JsonObject ReadMapping(Parser parser, State state, string text, MappingStart start)
    {
        var mapping = new JsonObject();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        state.Depth++;
        try
        {
            if (state.Depth > MaxDepth)
            {
                throw Violation(start.Start, $"the document is nested deeper than {MaxDepth.ToString(CultureInfo.InvariantCulture)} levels");
            }

            while (true)
            {
                Move(parser);
                if (parser.Current is MappingEnd)
                {
                    return mapping;
                }

                if (parser.Current is not Scalar key)
                {
                    throw Violation(parser.Current?.Start ?? default, "a mapping key must be a single value");
                }

                Count(state, key);
                RefuseNode(key);
                if (!ObeysCharacterRule(key.Value, out _))
                {
                    throw Violation(key.Start, "a key holds a character a manifest may not contain");
                }

                if (string.Equals(key.Value, "<<", StringComparison.Ordinal))
                {
                    throw Violation(key.Start, "the '<<' merge key is not allowed; write every member where it is used");
                }

                if (!keys.Add(key.Value))
                {
                    throw Violation(key.Start, "a key appears twice in one mapping");
                }

                Move(parser);
                mapping[key.Value] = ReadNode(parser, state, text);
            }
        }
        finally
        {
            state.Depth--;
        }
    }

    private static JsonArray ReadSequence(Parser parser, State state, string text, SequenceStart start)
    {
        var sequence = new JsonArray();
        state.Depth++;
        try
        {
            if (state.Depth > MaxDepth)
            {
                throw Violation(start.Start, $"the document is nested deeper than {MaxDepth.ToString(CultureInfo.InvariantCulture)} levels");
            }

            while (true)
            {
                Move(parser);
                if (parser.Current is SequenceEnd)
                {
                    return sequence;
                }

                sequence.Add(ReadNode(parser, state, text));
            }
        }
        finally
        {
            state.Depth--;
        }
    }

    /// <summary>A node's type and its explicit tag, refused as the subset refuses them.</summary>
    private static void RefuseNode(NodeEvent node)
    {
        if (!node.Anchor.IsEmpty)
        {
            throw Violation(node.Start, "anchors and aliases are not allowed; write the value where it is used");
        }

        if (!node.Tag.IsEmpty)
        {
            throw Violation(node.Start, "explicit tags are not allowed; quote the value instead");
        }
    }

    private static void RefuseDirectives(DocumentStart start)
    {
        if (start.Version is not null)
        {
            throw Violation(start.Start, "a %YAML directive is not allowed");
        }

        if (start.Tags is { } tags)
        {
            foreach (var directive in tags)
            {
                if (directive.Handle is not ("!" or "!!"))
                {
                    throw Violation(start.Start, "a %TAG directive is not allowed");
                }
            }
        }
    }

    private static void Count(State state, ParsingEvent node)
    {
        state.Nodes++;
        if (state.Nodes > MaxNodes)
        {
            throw Violation(node.Start, $"the document holds more than {MaxNodes.ToString(CultureInfo.InvariantCulture)} values");
        }
    }

    /// <summary>The value a scalar carries: text as written, or the type the core schema reads.</summary>
    private static JsonNode? Resolve(Scalar scalar, string text)
    {
        if (!ObeysCharacterRule(scalar.Value, out _))
        {
            throw Violation(scalar.Start, "the text holds a character a manifest may not contain");
        }

        if (scalar.Style is not ScalarStyle.Plain)
        {
            // Quoted and block scalars are text, whatever they look like.
            return JsonValue.Create(scalar.Value);
        }

        switch (scalar.Value)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }

        if (IsJsonNumber(scalar.Value))
        {
            // Parsed as JSON so the digits are written as the author wrote them: a
            // decimal setting never passes through a binary floating-point value.
            return JsonNode.Parse(scalar.Value)
                ?? throw Violation(scalar.Start, "a number could not be read");
        }

        if (IsNumberJsonCannotWrite(scalar.Value))
        {
            throw Violation(scalar.Start, "a number in a form JSON cannot write; write a decimal number or quote the value");
        }

        return JsonValue.Create(scalar.Value);
    }

    private static bool IsJsonNumber(string value) => JsonNumber().IsMatch(value);

    private static bool IsNumberJsonCannotWrite(string value) => NotJsonNumber().IsMatch(value);

    private static void Move(Parser parser)
    {
        if (!parser.MoveNext())
        {
            throw Violation(default, NotDocument);
        }
    }

    private static YamlException Violation(Mark mark, string rule) => new(mark, mark, rule);

    private static string Refusal(Mark mark, string text, string rule) => RefusalAt(Position(text, (int)Math.Clamp(mark.Index, 0L, int.MaxValue)), rule);

    private static string RefusalAt(string position, string rule) => $"{FileName} {position}: {rule}";

    /// <summary>The 1-based line and column of a character index, as every refusal names them.</summary>
    private static string Position(string text, int charIndex)
    {
        var index = Math.Clamp(charIndex, 0, text.Length);
        var line = 1;
        var lineStart = 0;
        for (var scan = 0; scan < index; scan++)
        {
            if (text[scan] == '\n')
            {
                line++;
                lineStart = scan + 1;
            }
        }

        var column = index - lineStart + 1;
        return $"line {line.ToString(CultureInfo.InvariantCulture)}, column {column.ToString(CultureInfo.InvariantCulture)}";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex JsonNumber();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(\+[0-9]|[-+]?0[0-9]+$|[-+]?\.[0-9]+$|[-+]?[0-9]+\.$|[-+]?0[xX][0-9a-fA-F]+$|[-+]?0[oO][0-7]+$|[-+]?0[bB][01]+$|[-+]?\.(inf|Inf|INF)$|\.(nan|NaN|NAN)$)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex NotJsonNumber();

    /// <summary>The counters one parse keeps: how deep it is and how much it has read.</summary>
    private sealed class State
    {
        public int Depth { get; set; }

        public int Nodes { get; set; }
    }
}
