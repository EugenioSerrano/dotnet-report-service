using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReportService.DataContract;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.Sample;

/// <summary>Where the at-bound record came from.</summary>
public enum AtBoundDerivation
{
    /// <summary>The contract admits the bound in no position, so nothing is printed.</summary>
    None,

    /// <summary>The record is the sample's own first record, with one position replaced.</summary>
    SampleRecord,

    /// <summary>The record is synthesised from the contract, every declared field at a valid value.</summary>
    Contract,
}

/// <summary>What one at-bound snapshot is built from: the version, its contract and its own sample.</summary>
/// <param name="Candidate">The version being taken.</param>
/// <param name="Contract">The version's prepared data contract, from the schema check or compiled here.</param>
/// <param name="SchemaRoot">The schema document, as the package holds it.</param>
/// <param name="SampleJson">The bytes of the version's own <c>sample.json</c>.</param>
/// <param name="BoundBytes">The row bound the value must occupy exactly.</param>
/// <param name="ItemAnchors">The item anchors the manifest declares, as field paths relative to the record.</param>
public sealed record AtBoundSampleInput(
    TemplateCandidate Candidate,
    PreparedDataContract Contract,
    JsonNode SchemaRoot,
    ReadOnlyMemory<byte> SampleJson,
    int BoundBytes,
    IReadOnlyList<string> ItemAnchors);

/// <summary>
/// What the at-bound snapshot is: the document to print - the version's own header and
/// one record, one position of which carries a string of exactly the bound - or the
/// verdict that the contract admits the bound in no position. The observation the
/// check records is carried beside it: the chosen position and its shape, the
/// derivation, the record's wire size, every position skipped and the cap that
/// skipped it, whether the value is anchor-bound, and the value's hash - never a value.
/// </summary>
public sealed record AtBoundSampleResult(
    byte[]? Document,
    AtBoundPosition? Chosen,
    AtBoundDerivation Derivation,
    long RecordBytes,
    IReadOnlyList<AtBoundPosition> Skipped,
    bool AnchorBound,
    string ValueSha256,
    string? NotApplicable)
{
    /// <summary>Whether the contract admits the bound somewhere and a document was built.</summary>
    public bool Applies => Document is not null;
}

/// <summary>
/// The derivation could not produce a record the contract admits: the builder's own
/// limitation, never a refusal of the version. The class names which of the
/// derivation's ways ended, and the message names positions and counts, never a value.
/// </summary>
public sealed class AtBoundDerivationException : Exception
{
    /// <summary>Creates the exception for the way the derivation ended.</summary>
    /// <param name="code">The derivation's own class, never a value.</param>
    /// <param name="message">What the derivation could not produce.</param>
    public AtBoundDerivationException(string code, string message)
        : base(message) => Code = code;

    /// <summary>The class of the derivation's own limitation.</summary>
    public string Code { get; }
}

/// <summary>
/// The at-bound snapshot, derived from the version and never from a fixture: the
/// version's own sample supplies the record where it has one, its data contract
/// supplies it everywhere the sample does not, and the header is always the
/// version's own. One position of the record - the first the contract admits the
/// bound at, anchors first - carries the at-bound value; the record's wire size is
/// measured against the 1 MiB maximum and an over-limit sample-derived record falls
/// back to the contract-synthesised one, so an over-limit record of this gate's own
/// making is never surfaced as a refusal of the template. The whole document is then
/// validated by the delivered schema check: that validation, not the construction,
/// is what makes it schema-valid.
/// </summary>
public static class AtBoundSample
{
    /// <summary>The most bytes of record content one block holds, as the record maximum.</summary>
    public const long RecordMaximumBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Writing = new()
    {
        // The document is read by the data reader, never embedded in a page; the writer
        // keeps the characters it was given and writes no indentation, so the record's
        // measured wire size is the bytes the reader will copy.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Builds the at-bound snapshot, or answers that the contract admits the bound nowhere.</summary>
    /// <param name="input">The version, its contract and its own sample.</param>
    /// <returns>The document to print, or the verdict that no position admits the bound, with the observation.</returns>
    /// <exception cref="AtBoundDerivationException">The derivation cannot produce a record the contract admits.</exception>
    public static AtBoundSampleResult Build(AtBoundSampleInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new Derivation(input).Run();
    }

    /// <summary>One derivation over one version: its schema, its sample and the record it builds.</summary>
    private sealed class Derivation(AtBoundSampleInput input)
    {
        private readonly JsonNode _root = input.SchemaRoot;
        private readonly JsonObject _sample = ParseDocument(input.SampleJson);
        private readonly JsonNode _recordSchema = RecordSchema(input.SchemaRoot)
            ?? throw new AtBoundDerivationException(
                "record-root",
                "the schema declares no record subschema at #/properties/items/items.");

        private readonly JsonObject? _firstRecord = FirstRecord(ParseDocument(input.SampleJson));

        public AtBoundSampleResult Run()
        {
            var positions = AtBoundPositions.Walk(
                _root,
                input.BoundBytes,
                (position, value) => Confirm(position, value));
            var ordered = Order(positions);

            AtBoundPosition? chosen = null;
            var skipped = new List<AtBoundPosition>();
            foreach (var position in ordered)
            {
                if (position.Admits == true && position.Cap is null && position.Undecided is null)
                {
                    chosen = position;
                    break;
                }

                skipped.Add(position with
                {
                    Undecided = position.Undecided ?? (position.Cap is null ? "schema" : null),
                });
            }

            if (chosen is null)
            {
                var nothingToPrint = positions.Count == 0 || positions.All(position => position.Cap is not null);
                if (nothingToPrint)
                {
                    return new AtBoundSampleResult(
                        Document: null,
                        Chosen: null,
                        Derivation: AtBoundDerivation.None,
                        RecordBytes: 0,
                        Skipped: skipped,
                        AnchorBound: false,
                        ValueSha256: string.Empty,
                        NotApplicable: NotApplicableOf(positions));
                }

                throw new AtBoundDerivationException(
                    "undecided",
                    "no position of the record's contract could be decided as admitting the bound, and not every position is capped: "
                    + string.Join(
                        "; ",
                        ordered.Select(position => position.Path + "=" + (position.Cap ?? position.Undecided ?? "undecided"))));
            }

            var value = chosen.Value
                ?? throw new AtBoundDerivationException("value", "the chosen position carries no built value.");
            var derivation = _firstRecord is null ? AtBoundDerivation.Contract : AtBoundDerivation.SampleRecord;
            var record = BuildRecord(_firstRecord, chosen, value);
            var recordBytes = WireSize(record);
            if (recordBytes > RecordMaximumBytes)
            {
                // The sample's own record is the base only while the substituted value keeps it
                // inside one block; an over-limit record of this gate's own making is never a
                // refusal of the template, so the record is synthesised from the contract.
                derivation = AtBoundDerivation.Contract;
                record = BuildRecord(firstRecord: null, chosen, value);
                recordBytes = WireSize(record);
                if (recordBytes > RecordMaximumBytes)
                {
                    throw new AtBoundDerivationException(
                        "record-too-large",
                        "the record synthesised from the contract is larger than the 1 MiB record maximum.");
                }
            }

            var document = Compose(record);
            Validate(chosen, record, document);

            return new AtBoundSampleResult(
                Document: document,
                Chosen: chosen,
                Derivation: derivation,
                RecordBytes: recordBytes,
                Skipped: skipped,
                AnchorBound: input.ItemAnchors.Any(anchor => string.Equals(anchor, chosen.Path, StringComparison.Ordinal)),
                ValueSha256: AtBoundText.Sha256(value),
                NotApplicable: null);
        }

        private bool Confirm(AtBoundPosition position, string value)
        {
            try
            {
                var record = BuildRecord(_firstRecord, position, value);
                return input.Contract.CheckRecord(Wire(record)).IsValid;
            }
            catch (AtBoundDerivationException)
            {
                return false;
            }
        }

        private void Validate(AtBoundPosition chosen, JsonObject record, byte[] document)
        {
            var recordCheck = input.Contract.CheckRecord(Wire(record));
            if (!recordCheck.IsValid)
            {
                throw new AtBoundDerivationException(
                    RefusalClass(recordCheck, chosen.Path, "$"),
                    "the record the derivation built is refused by the version's own schema at "
                    + (recordCheck.Path ?? "$") + " (" + (recordCheck.Keyword ?? "schema") + ").");
            }

            var envelopeCheck = input.Contract.CheckEnvelope(document);
            if (!envelopeCheck.IsValid)
            {
                throw new AtBoundDerivationException(
                    RefusalClass(envelopeCheck, chosen.Path, "$.items[0]"),
                    "the document the derivation built is refused by the version's own schema at "
                    + (envelopeCheck.Path ?? "$") + " (" + (envelopeCheck.Keyword ?? "schema") + ").");
            }
        }

        private static string RefusalClass(ContractCheck check, string positionPath, string prefix)
        {
            var expected = prefix == "$" ? positionPath : prefix + positionPath[1..];
            var path = check.Path ?? "$";
            var own = string.Equals(path, expected, StringComparison.Ordinal)
                || path.StartsWith(expected + ".", StringComparison.Ordinal)
                || path.StartsWith(expected + "[", StringComparison.Ordinal);
            return own ? "position-refused" : "candidate-refused";
        }

        private JsonObject BuildRecord(JsonObject? firstRecord, AtBoundPosition position, string value)
        {
            var record = firstRecord is null
                ? SynthesiseObject(_recordSchema)
                : (JsonObject)firstRecord.DeepClone();

            foreach (var condition in position.Conditions)
            {
                record[condition.Member] = condition.Value?.DeepClone();
            }

            return Assign(record, _recordSchema, position.Steps, 0, value) as JsonObject
                ?? throw new AtBoundDerivationException("assign", "the position could not be placed in the record.");
        }

        private JsonNode? Assign(
            JsonNode? current,
            JsonNode? schema,
            IReadOnlyList<AtBoundStep> steps,
            int index,
            string value)
        {
            if (index == steps.Count)
            {
                return JsonValue.Create(value);
            }

            var step = steps[index];
            var resolved = Resolve(schema);
            if (step.MemberName is { } name)
            {
                var target = current as JsonObject
                    ?? throw new AtBoundDerivationException("assign", "a member position sits inside a value that is not an object.");
                var memberSchema = MemberSchema(resolved, name);
                var child = target[name];
                if (child is null)
                {
                    if (IsArraySchema(memberSchema))
                    {
                        child = new JsonArray();
                    }
                    else if (IsObjectSchema(memberSchema))
                    {
                        child = SynthesiseObject(memberSchema!);
                    }

                    if (child is not null)
                    {
                        target[name] = child;
                    }
                }

                var assigned = Assign(child, memberSchema, steps, index + 1, value);
                if (!ReferenceEquals(assigned, child))
                {
                    target[name] = assigned;
                }

                return target;
            }

            var array = current as JsonArray
                ?? throw new AtBoundDerivationException("assign", "an element position sits inside a value that is not an array.");
            var position = step.Index!.Value;
            var elementSchema = ElementSchema(resolved, position);
            while (array.Count <= position)
            {
                array.Add(null);
            }

            var element = array[position];
            if (element is null)
            {
                if (IsArraySchema(elementSchema))
                {
                    element = new JsonArray();
                }
                else if (IsObjectSchema(elementSchema))
                {
                    element = SynthesiseObject(elementSchema!);
                }

                if (element is not null)
                {
                    array[position] = element;
                }
            }

            var placed = Assign(element, elementSchema, steps, index + 1, value);
            if (!ReferenceEquals(placed, element))
            {
                array[position] = placed;
            }

            return array;
        }

        private JsonObject SynthesiseObject(JsonNode schema)
        {
            var resolved = Resolve(schema) as JsonObject ?? new JsonObject();
            var target = new JsonObject();
            foreach (var (name, memberSchema) in PropertiesOf(resolved))
            {
                target[name] = ValueFor(memberSchema);
            }

            foreach (var branch in BranchesOf(resolved))
            {
                foreach (var (name, memberSchema) in PropertiesOf(branch))
                {
                    target[name] ??= ValueFor(memberSchema);
                }
            }

            foreach (var name in RequiredOf(resolved).Concat(BranchesOf(resolved).SelectMany(RequiredOf)))
            {
                if (target[name] is null)
                {
                    target[name] = JsonValue.Create("x");
                }
            }

            return target;
        }

        private JsonNode? ValueFor(JsonNode? schema)
        {
            if (Resolve(schema) is not JsonObject resolved)
            {
                return JsonValue.Create("x");
            }

            if (resolved["const"] is JsonNode constant)
            {
                return constant.DeepClone();
            }

            if (resolved["enum"] is JsonArray values && values.Count > 0)
            {
                return values[0]?.DeepClone();
            }

            var branches = BranchesOf(resolved).ToList();
            if (branches.Count > 0)
            {
                var merged = new JsonObject();
                foreach (var branch in branches)
                {
                    foreach (var (name, memberSchema) in PropertiesOf(branch))
                    {
                        merged[name] ??= ValueFor(memberSchema);
                    }
                }

                if (merged.Count > 0)
                {
                    return merged;
                }
            }

            return SingleTypeOf(resolved) switch
            {
                "string" => JsonValue.Create(StringValue(resolved)),
                "integer" => JsonValue.Create(IntegerValue(resolved)),
                "number" => JsonValue.Create(NumberValue(resolved)),
                "boolean" => JsonValue.Create(false),
                "null" => null,
                "array" => new JsonArray(),
                "object" => SynthesiseObject(resolved),
                _ => resolved["properties"] is JsonObject || resolved["required"] is JsonArray
                    ? SynthesiseObject(resolved)
                    : JsonValue.Create("x"),
            };
        }

        private static string StringValue(JsonObject schema)
        {
            if (schema["format"] is JsonValue format && format.GetValueKind() == JsonValueKind.String)
            {
                switch (format.GetValue<string>())
                {
                    case "date":
                        return "2026-01-01";
                    case "date-time":
                        return "2026-01-01T00:00:00Z";
                    case "time":
                        return "00:00:00";
                    case "duration":
                        return "P1D";
                    case "email":
                        return "a@example.com";
                    case "uri":
                        return "https://example.com/";
                    case "uuid":
                        return "00000000-0000-4000-8000-000000000000";
                    case "ipv4":
                        return "0.0.0.0";
                    case "ipv6":
                        return "0:0:0:0:0:0:0:1";
                }
            }

            if (schema["pattern"] is JsonValue pattern
                && pattern.GetValueKind() == JsonValueKind.String
                && AtBoundText.BuildForPattern(pattern.GetValue<string>(), 8) is { } built)
            {
                return built;
            }

            return "x";
        }

        private static long IntegerValue(JsonObject schema)
        {
            if (schema["minimum"] is JsonValue minimum && minimum.TryGetValue<long>(out var low))
            {
                return low;
            }

            if (schema["exclusiveMinimum"] is JsonValue exclusive && exclusive.TryGetValue<long>(out var above))
            {
                return above + 1;
            }

            return 0;
        }

        private static decimal NumberValue(JsonObject schema)
        {
            if (schema["minimum"] is JsonValue minimum && minimum.TryGetValue<decimal>(out var low))
            {
                return low;
            }

            return 0m;
        }

        private static IEnumerable<(string Name, JsonNode? Schema)> PropertiesOf(JsonObject schema)
        {
            if (schema["properties"] is JsonObject properties)
            {
                foreach (var (name, memberSchema) in properties)
                {
                    yield return (name, memberSchema);
                }
            }
        }

        private static IEnumerable<string> RequiredOf(JsonObject schema)
        {
            if (schema["required"] is JsonArray required)
            {
                foreach (var name in required)
                {
                    if (name is not null && name.GetValueKind() == JsonValueKind.String)
                    {
                        yield return name.GetValue<string>();
                    }
                }
            }
        }

        private IEnumerable<JsonObject> BranchesOf(JsonObject schema)
        {
            if (schema["allOf"] is not JsonArray branches)
            {
                yield break;
            }

            foreach (var branch in branches)
            {
                if (Resolve(branch) is JsonObject resolved)
                {
                    yield return resolved;
                }
            }
        }

        private JsonNode? MemberSchema(JsonNode? schema, string name)
        {
            if (schema is not JsonObject obj)
            {
                return null;
            }

            if (obj["properties"] is JsonObject properties && properties[name] is { } declared)
            {
                return declared;
            }

            if (obj["patternProperties"] is JsonObject patterns)
            {
                foreach (var (pattern, subschema) in patterns)
                {
                    if (Matches(pattern, name))
                    {
                        return subschema;
                    }
                }
            }

            foreach (var keyword in new[] { "additionalProperties", "unevaluatedProperties" })
            {
                if (obj[keyword] is JsonObject open)
                {
                    return open;
                }
            }

            foreach (var branch in BranchesOf(obj))
            {
                if (MemberSchema(branch, name) is { } fromBranch)
                {
                    return fromBranch;
                }
            }

            return null;
        }

        private JsonNode? ElementSchema(JsonNode? schema, int index)
        {
            if (schema is not JsonObject obj)
            {
                return null;
            }

            if (obj["prefixItems"] is JsonArray prefix && index < prefix.Count)
            {
                return prefix[index];
            }

            if (obj["items"] is JsonObject items)
            {
                return items;
            }

            foreach (var branch in BranchesOf(obj))
            {
                if (ElementSchema(branch, index) is { } fromBranch)
                {
                    return fromBranch;
                }
            }

            return null;
        }

        private bool IsObjectSchema(JsonNode? schema) =>
            Resolve(schema) is JsonObject obj
            && (SingleTypeOf(obj) == "object" || (obj["properties"] is JsonObject && SingleTypeOf(obj) is null));

        private bool IsArraySchema(JsonNode? schema) =>
            Resolve(schema) is JsonObject obj
            && (SingleTypeOf(obj) == "array" || (SingleTypeOf(obj) is null && obj["items"] is not null));

        private static string? SingleTypeOf(JsonObject schema)
        {
            if (schema["type"] is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            {
                return value.GetValue<string>();
            }

            if (schema["type"] is JsonArray many)
            {
                var types = many
                    .Where(item => item is not null && item.GetValueKind() == JsonValueKind.String)
                    .Select(item => item!.GetValue<string>())
                    .Where(name => !string.Equals(name, "null", StringComparison.Ordinal))
                    .ToList();
                return types.Count == 1 ? types[0] : null;
            }

            return null;
        }

        private static bool Matches(string pattern, string value)
        {
            try
            {
                return System.Text.RegularExpressions.Regex.IsMatch(
                    value,
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private JsonNode? Resolve(JsonNode? schema)
        {
            var node = schema;
            var guard = 0;
            while (node is JsonObject obj
                && obj["$ref"] is JsonValue reference
                && reference.GetValueKind() == JsonValueKind.String
                && reference.GetValue<string>() is { } pointer
                && pointer.StartsWith("#/", StringComparison.Ordinal))
            {
                if (++guard > AtBoundPositions.DepthLimit)
                {
                    return null;
                }

                node = ResolvePointer(_root, pointer);
            }

            return node;
        }

        private IReadOnlyList<AtBoundPosition> Order(IReadOnlyList<AtBoundPosition> positions)
        {
            var recordOrder = new Dictionary<string, int>(StringComparer.Ordinal);
            if (_firstRecord is not null)
            {
                var index = 0;
                foreach (var (name, _) in _firstRecord)
                {
                    recordOrder[name] = index++;
                }
            }

            return positions
                .Select((position, index) => (Position: position, Index: index))
                .OrderBy(entry => AnchorIndex(entry.Position.Path))
                .ThenBy(entry => FirstMemberOrder(entry.Position, recordOrder))
                .ThenBy(entry => entry.Index)
                .Select(entry => entry.Position)
                .ToList();
        }

        private int AnchorIndex(string path)
        {
            for (var index = 0; index < input.ItemAnchors.Count; index++)
            {
                if (string.Equals(input.ItemAnchors[index], path, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return int.MaxValue;
        }

        private static int FirstMemberOrder(AtBoundPosition position, IReadOnlyDictionary<string, int> recordOrder)
        {
            if (position.Steps.Count == 0 || position.Steps[0].MemberName is not { } name)
            {
                return int.MaxValue;
            }

            return recordOrder.TryGetValue(name, out var order) ? order : int.MaxValue;
        }

        private static string NotApplicableOf(IReadOnlyList<AtBoundPosition> positions) =>
            positions.Count == 0
                ? "the record's contract declares no string position."
                : "every position of the record's contract is capped: "
                    + string.Join(
                        "; ",
                        positions.Select(position => position.Path + " (" + (position.Cap ?? "unknown") + ")"));

        private byte[] Compose(JsonObject record)
        {
            var document = (JsonObject)_sample.DeepClone();
            document["items"] = new JsonArray(record.DeepClone());
            return Wire(document);
        }
    }

    private static JsonNode? ResolvePointer(JsonNode? root, string pointer)
    {
        if (root is null)
        {
            return null;
        }

        JsonNode? node = root;
        foreach (var segment in pointer.TrimStart('/').Split('/'))
        {
            var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node switch
            {
                JsonObject obj when obj[name] is { } child => child,
                JsonArray array when int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                    && index < array.Count => array[index],
                _ => null,
            };

            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    private static JsonNode? RecordSchema(JsonNode schemaRoot)
    {
        var items = ResolvePointer(schemaRoot, "/properties/items");
        return items is null ? null : ResolvePointer(items, "/items");
    }

    private static JsonObject ParseDocument(ReadOnlyMemory<byte> sampleJson)
    {
        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(sampleJson.Span)) as JsonObject
                ?? throw new AtBoundDerivationException("sample-document", "the sample is not a document.");
        }
        catch (JsonException)
        {
            throw new AtBoundDerivationException("sample-document", "the sample is not a document.");
        }
    }

    private static JsonObject? FirstRecord(JsonObject sample) =>
        sample["items"] is JsonArray items && items.Count > 0 && items[0] is JsonObject record
            ? record
            : null;

    private static byte[] Wire(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(Writing));

    private static long WireSize(JsonNode node) => Wire(node).LongLength;
}
