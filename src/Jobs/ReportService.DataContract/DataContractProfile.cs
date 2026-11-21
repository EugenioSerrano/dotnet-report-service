using System.Collections.Frozen;

namespace ReportService.DataContract;

/// <summary>
/// The restricted profile a template's data contract must stay inside. One forward
/// pass reads the schema and records where its schemas and keywords are; the rules are
/// then applied in a fixed order and the first one that fails is reported with a place
/// inside the schema. A hostile document is refused, never thrown over.
/// </summary>
public static class DataContractProfile
{
    /// <summary>The most bytes a schema document may occupy.</summary>
    public const int MaximumBytes = 65_536;

    /// <summary>The most levels of schema positions a schema may nest.</summary>
    public const int MaximumDepth = 64;

    /// <summary>The most characters a pattern may occupy.</summary>
    public const int MaximumPatternCharacters = 1_024;

    /// <summary>The dialect the profile requires.</summary>
    public const string Dialect = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>The formats the contract asserts, and the only ones a schema may name.</summary>
    public static IReadOnlyList<string> Formats { get; } =
    [
        "date",
        "date-time",
        "time",
        "duration",
        "email",
        "uri",
        "uuid",
        "ipv4",
        "ipv6",
    ];

    /// <summary>Checks a schema document against the profile.</summary>
    /// <param name="schema">The schema document, as the package holds it.</param>
    /// <returns>The verdict, naming the rule and the place of the first rule that failed.</returns>
    public static ProfileResult Check(ReadOnlyMemory<byte> schema) => Analyse(schema).Result;

    /// <summary>Reads a schema document and applies the profile's rules to what it found.</summary>
    /// <param name="schema">The schema document, as the package holds it.</param>
    internal static SchemaScan Analyse(ReadOnlyMemory<byte> schema) => SchemaScan.Read(schema);
}

/// <summary>What one pass over a schema document found, and the profile's verdict over it.</summary>
internal sealed class SchemaScan
{
    private SchemaScan()
    {
    }

    /// <summary>Every member name some <c>properties</c> object of the document declares.</summary>
    public HashSet<string> DeclaredNames { get; } = new(StringComparer.Ordinal);

    /// <summary>The profile's verdict over this document.</summary>
    public ProfileResult Result { get; private set; } = ProfileResult.Valid;

    /// <summary>Reads <paramref name="schema"/> and applies the profile's rules in their fixed order.</summary>
    public static SchemaScan Read(ReadOnlyMemory<byte> schema)
    {
        var scan = new SchemaScan();
        if (schema.Length > DataContractProfile.MaximumBytes)
        {
            scan.Result = ProfileResult.Refused("size", "");
            return scan;
        }

        var reader = new System.Text.Json.Utf8JsonReader(
            schema.Span,
            new System.Text.Json.JsonReaderOptions { MaxDepth = DataContractProfile.MaximumDepth * 4 });

        try
        {
            if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.StartObject)
            {
                scan.Result = ProfileResult.Refused("dialect", "");
                return scan;
            }

            scan.ReadSchema(ref reader, "", 1, isRoot: true);
        }
        catch (System.Text.Json.JsonException)
        {
            scan.Result = ProfileResult.Refused("compile", scan._lastPointer);
            return scan;
        }

        scan.ApplyRules();
        return scan;
    }

    private void ApplyRules()
    {
        // (2) the dialect declares itself at the root
        if (!string.Equals(_rootDialect, DataContractProfile.Dialect, StringComparison.Ordinal))
        {
            Result = ProfileResult.Refused("dialect", "/$schema");
            return;
        }

        // (3) every member of every schema position is admitted
        if (_firstMemberFailure is not null)
        {
            Result = ProfileResult.Refused("keyword", _firstMemberFailure);
            return;
        }

        if (_firstShapeFailure is not null)
        {
            Result = ProfileResult.Refused("schema", _firstShapeFailure);
            return;
        }

        // (4) the schema positions stay within the depth ceiling
        if (_firstDepthFailure is not null)
        {
            Result = ProfileResult.Refused("depth", _firstDepthFailure);
            return;
        }

        // (5) every pattern is short enough and compiles
        foreach (var (pointer, pattern) in _patterns)
        {
            if (pattern is null
                || pattern.Length > DataContractProfile.MaximumPatternCharacters
                || !Compiles(pattern))
            {
                Result = ProfileResult.Refused("pattern", pointer);
                return;
            }
        }

        // (6) every format is one the contract asserts
        foreach (var (pointer, format) in _formats)
        {
            if (format is null || !Formats.Contains(format))
            {
                Result = ProfileResult.Refused("format", pointer);
                return;
            }
        }

        // (7) every reference is a fragment of this document that resolves inside it
        foreach (var (pointer, reference) in _references)
        {
            if (reference is null || !reference.StartsWith("#/", StringComparison.Ordinal))
            {
                Result = ProfileResult.Refused("reference", pointer);
                return;
            }

            if (!_nodes.ContainsKey(reference[1..]))
            {
                Result = ProfileResult.Refused("reference", pointer);
                return;
            }
        }

        // (8) no cycle returns to a schema without descending into the value
        var cycle = FindCycle();
        if (cycle is not null)
        {
            Result = ProfileResult.Refused("cycle", cycle);
            return;
        }

        // (9) the envelope exposes both roots
        if (Resolve("/properties/header") is null)
        {
            Result = ProfileResult.Refused("envelope", "/properties/header");
            return;
        }

        var items = Resolve("/properties/items");
        if (items is null || Resolve(items + "/items") is null)
        {
            Result = ProfileResult.Refused("envelope", "/properties/items/items");
        }
    }

    private void ReadSchema(ref System.Text.Json.Utf8JsonReader reader, string pointer, int depth, bool isRoot)
    {
        if (_firstShapeFailure is not null || _firstMemberFailure is not null)
        {
            return;
        }

        if (depth > DataContractProfile.MaximumDepth)
        {
            _firstDepthFailure ??= pointer;
        }

        if (reader.TokenType is System.Text.Json.JsonTokenType.True or System.Text.Json.JsonTokenType.False)
        {
            _nodes[pointer] = new SchemaNode(IsBoolean: true, Reference: null);
            return;
        }

        if (reader.TokenType != System.Text.Json.JsonTokenType.StartObject)
        {
            _firstShapeFailure ??= pointer;
            return;
        }

        _nodes[pointer] = new SchemaNode(IsBoolean: false, Reference: null);

        while (reader.Read() && reader.TokenType == System.Text.Json.JsonTokenType.PropertyName)
        {
            var name = reader.GetString()!;
            var member = pointer + "/" + Escape(name);
            _lastPointer = member;

            if (!Admitted(name, isRoot))
            {
                _firstMemberFailure ??= member;
                reader.Skip();
                continue;
            }

            if (name == "$schema")
            {
                if (reader.Read() && reader.TokenType == System.Text.Json.JsonTokenType.String)
                {
                    _rootDialect = reader.GetString();
                }
                else
                {
                    _firstShapeFailure ??= member;
                }

                continue;
            }

            if (!reader.Read())
            {
                return;
            }

            switch (name)
            {
                case "$ref":
                    var reference = Text(ref reader);
                    _references.Add((member, reference));
                    _nodes[pointer] = _nodes[pointer] with { Reference = reference };
                    break;

                case "pattern":
                    _patterns.Add((member, Text(ref reader)));
                    break;

                case "format":
                    _formats.Add((member, Text(ref reader)));
                    break;

                case "properties":
                    ReadSchemaMap(ref reader, member, depth, declared: true, successors: null);
                    break;

                case "$defs":
                    ReadSchemaMap(ref reader, member, depth, declared: false, successors: null);
                    break;

                case "dependentSchemas":
                    ReadSchemaMap(ref reader, member, depth, declared: false, successors: Successors(pointer));
                    break;

                case "patternProperties":
                    ReadSchemaMap(ref reader, member, depth, declared: false, successors: null, patterns: true);
                    break;

                case "prefixItems":
                    ReadSchemaList(ref reader, member, depth, []);
                    break;

                case "allOf" or "anyOf" or "oneOf":
                    ReadSchemaList(ref reader, member, depth, Successors(pointer));
                    break;

                case "items" or "additionalProperties" or "unevaluatedProperties" or "propertyNames" or "contains":
                    ReadSchema(ref reader, member, depth + 1, isRoot: false);
                    break;

                case "not" or "if" or "then" or "else":
                    Successors(pointer).Add(member);
                    ReadSchema(ref reader, member, depth + 1, isRoot: false);
                    break;

                default:
                    reader.Skip();
                    break;
            }
        }
    }

    private void ReadSchemaMap(
        ref System.Text.Json.Utf8JsonReader reader,
        string pointer,
        int depth,
        bool declared,
        List<string>? successors,
        bool patterns = false)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartObject)
        {
            _firstShapeFailure ??= pointer;
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType == System.Text.Json.JsonTokenType.PropertyName)
        {
            var key = reader.GetString()!;
            var child = pointer + "/" + Escape(key);
            if (declared)
            {
                DeclaredNames.Add(key);
            }

            if (patterns)
            {
                _patterns.Add((child, key));
            }

            if (!reader.Read())
            {
                return;
            }

            successors?.Add(child);
            ReadSchema(ref reader, child, depth + 1, isRoot: false);
        }
    }

    private void ReadSchemaList(
        ref System.Text.Json.Utf8JsonReader reader,
        string pointer,
        int depth,
        List<string> successors)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartArray)
        {
            _firstShapeFailure ??= pointer;
            reader.Skip();
            return;
        }

        var index = 0;
        while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndArray)
        {
            var child = pointer + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            index++;
            successors.Add(child);
            ReadSchema(ref reader, child, depth + 1, isRoot: false);
        }
    }

    private List<string> Successors(string pointer)
    {
        if (!_successors.TryGetValue(pointer, out var list))
        {
            list = [];
            _successors[pointer] = list;
        }

        return list;
    }

    private string? FindCycle()
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var start in _nodes.Keys)
        {
            if (WalksBackTo(start, state))
            {
                return start;
            }
        }

        return null;
    }

    private bool WalksBackTo(string pointer, Dictionary<string, int> state)
    {
        if (state.TryGetValue(pointer, out var visited))
        {
            return visited == 1;
        }

        if (!_nodes.TryGetValue(pointer, out var node))
        {
            return false;
        }

        state[pointer] = 1;
        var next = new List<string>();
        if (node.Reference is { Length: > 1 } reference && reference.StartsWith("#/", StringComparison.Ordinal))
        {
            next.Add(reference[1..]);
        }

        if (_successors.TryGetValue(pointer, out var successors))
        {
            next.AddRange(successors);
        }

        foreach (var child in next)
        {
            if (WalksBackTo(child, state))
            {
                return true;
            }
        }

        state[pointer] = 2;
        return false;
    }

    private string? Resolve(string pointer)
    {
        var current = pointer;
        for (var hop = 0; hop < 32; hop++)
        {
            if (!_nodes.TryGetValue(current, out var node))
            {
                return null;
            }

            if (node.Reference is not { Length: > 1 } reference
                || !reference.StartsWith("#/", StringComparison.Ordinal))
            {
                return current;
            }

            current = reference[1..];
        }

        return null;
    }

    private static bool Admitted(string name, bool isRoot) => name switch
    {
        // structural members
        "$schema" => isRoot,
        "$ref" or "$defs" or "$comment" => true,

        // assertions and applicators
        "type" or "enum" or "const" or "properties" or "required"
            or "additionalProperties" or "unevaluatedProperties" or "patternProperties" or "propertyNames"
            or "items" or "prefixItems" or "contains" or "minContains" or "maxContains"
            or "minItems" or "maxItems" or "uniqueItems"
            or "minLength" or "maxLength" or "pattern"
            or "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" or "multipleOf"
            or "allOf" or "anyOf" or "oneOf" or "not" or "if" or "then" or "else"
            or "dependentRequired" or "dependentSchemas" or "format" => true,

        // annotations
        "title" or "description" or "default" or "examples" or "deprecated" or "readOnly" or "writeOnly" => true,

        _ => false,
    };

    private static string? Text(ref System.Text.Json.Utf8JsonReader reader) =>
        reader.TokenType == System.Text.Json.JsonTokenType.String ? reader.GetString() : null;

    private static bool Compiles(string pattern)
    {
        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Escape(string name) =>
        name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    internal static FrozenSet<string> AssertionKeywords { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "type", "enum", "const", "required", "additionalProperties", "unevaluatedProperties", "propertyNames",
        "minItems", "maxItems", "uniqueItems", "contains", "minContains", "maxContains",
        "minLength", "maxLength", "pattern", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
        "multipleOf", "dependentRequired", "format", "not", "oneOf", "anyOf",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Formats =
        DataContractProfile.Formats.ToFrozenSet(StringComparer.Ordinal);

    private readonly Dictionary<string, SchemaNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _successors = new(StringComparer.Ordinal);
    private readonly List<(string Pointer, string? Value)> _patterns = [];
    private readonly List<(string Pointer, string? Value)> _formats = [];
    private readonly List<(string Pointer, string? Value)> _references = [];
    private string? _rootDialect;
    private string? _firstMemberFailure;
    private string? _firstShapeFailure;
    private string? _firstDepthFailure;
    private string _lastPointer = "";

    private readonly record struct SchemaNode(bool IsBoolean, string? Reference);
}
