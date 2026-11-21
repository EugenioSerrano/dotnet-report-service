using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReportService.TemplateChecks.Sample;

/// <summary>The keyword of the contract that supplied one position of a record's contract.</summary>
public enum AtBoundPositionShape
{
    /// <summary>A string member of the record itself.</summary>
    RootMember,

    /// <summary>A string member of a nested object.</summary>
    NestedMember,

    /// <summary>A string element of an array.</summary>
    ArrayElement,

    /// <summary>A string member of an object element of an array.</summary>
    ArrayElementMember,

    /// <summary>A <c>prefixItems</c> element.</summary>
    TupleElement,

    /// <summary>A member <c>additionalProperties</c> admits.</summary>
    OpenAdditional,

    /// <summary>A member <c>patternProperties</c> admits.</summary>
    OpenPattern,

    /// <summary>A member <c>unevaluatedProperties</c> admits.</summary>
    OpenUnevaluated,

    /// <summary>The string branch of a nullable or union field.</summary>
    UnionBranch,

    /// <summary>A position reached through an <c>allOf</c> branch.</summary>
    AllOfBranch,

    /// <summary>A position reached through the <c>then</c> branch of an <c>if</c>.</summary>
    ConditionalThen,

    /// <summary>A position reached through the <c>else</c> branch of an <c>if</c>.</summary>
    ConditionalElse,

    /// <summary>A position reached through a <c>dependentSchemas</c> subschema.</summary>
    DependentSchema,
}

/// <summary>One step of a position's path: a member name or an array position.</summary>
public sealed record AtBoundStep
{
    private AtBoundStep(string? memberName, int? index)
    {
        MemberName = memberName;
        Index = index;
    }

    /// <summary>The member name, when the step is a member.</summary>
    public string? MemberName { get; }

    /// <summary>The array position, when the step is an element.</summary>
    public int? Index { get; }

    /// <summary>A member step.</summary>
    /// <param name="name">The member's name.</param>
    public static AtBoundStep Member(string name) => new(name ?? throw new ArgumentNullException(nameof(name)), null);

    /// <summary>An element step.</summary>
    /// <param name="index">The element's position.</param>
    public static AtBoundStep Element(int index) => new(null, index);
}

/// <summary>A member the record must carry for a position to be reachable, at the value it must carry.</summary>
/// <param name="Member">The member of the record.</param>
/// <param name="Value">The value it is materialised at.</param>
public sealed record AtBoundCondition(string Member, JsonNode? Value);

/// <summary>
/// One position at which a record's contract admits a string value: its path in the
/// form the diagnostics carry, the shape that supplied it, how to reach it in a
/// record, the constraints its own schema imposes, and whether the contract admits a
/// value of exactly the bound there - or the cap that refuses it, or the reason the
/// builder could not decide it.
/// </summary>
public sealed record AtBoundPosition
{
    /// <summary>The path relative to the record, in the <c>$.name</c> form.</summary>
    public required string Path { get; init; }

    /// <summary>The shape that supplied the position.</summary>
    public required AtBoundPositionShape Shape { get; init; }

    /// <summary>The steps that reach the position inside the record.</summary>
    public required IReadOnlyList<AtBoundStep> Steps { get; init; }

    /// <summary>The position's order in a depth-first walk of the contract.</summary>
    public int Order { get; init; }

    /// <summary>The value the contract admits here, when one was built.</summary>
    public string? Value { get; init; }

    /// <summary>Whether the delivered schema check confirmed the built value at this position.</summary>
    public bool? Admits { get; init; }

    /// <summary>The cap that proves the contract admits no value at the bound here.</summary>
    public string? Cap { get; init; }

    /// <summary>The reason the builder could not decide this position.</summary>
    public string? Undecided { get; init; }

    /// <summary>The members the record must carry for the position to be reachable.</summary>
    public IReadOnlyList<AtBoundCondition> Conditions { get; init; } = [];

    /// <summary>Whether the contract admits exactly the bound at this position.</summary>
    public bool AdmitsTheBound => Cap is null && Undecided is null && Value is not null && Admits != false;

    /// <summary>Whether the contract definitely admits nothing at this position.</summary>
    public bool IsCapped => Cap is not null;
}

/// <summary>The walk met a shape of the contract it cannot read, and the value is not judged.</summary>
public sealed class AtBoundWalkException : Exception
{
    /// <summary>Creates the exception for the shape that stopped the walk.</summary>
    /// <param name="code">The walk's own class, never a value.</param>
    /// <param name="message">What the contract declares that the walk cannot read.</param>
    public AtBoundWalkException(string code, string message)
        : base(message) => Code = code;

    /// <summary>The class of the shape that stopped the walk.</summary>
    public string Code { get; }
}

/// <summary>
/// The total walk over a record's data contract: every position at which the
/// contract can admit a string value, at any depth - the record's own member, a
/// member of a nested object, an array element, a member an open record admits, the
/// string branch of a nullable or union field, and a position the contract reaches
/// only through a composition, a conditional, a dependent schema or a tuple -
/// read keyword by keyword over the set the data-contract dialect admits.
/// <para>
/// The walk is a claim about the contract and never about one built value: a
/// <c>maxLength</c> below the bound, a <c>minLength</c> above it or a bounded
/// <c>format</c> caps a position; a <c>const</c>/<c>enum</c> admits only through a
/// member of exactly the bound; and a <c>pattern</c> or a long-value <c>format</c>
/// admits only through a value the builder constructs and the caller confirms with
/// the delivered schema check. A position the builder cannot construct is
/// <em>undecided</em>, never silently capped and never a reason to claim the
/// contract admits nothing.
/// </para>
/// </summary>
public static class AtBoundPositions
{
    /// <summary>The depth ceiling of ADR-019's profile, in schema levels.</summary>
    public const int DepthLimit = 64;

    /// <summary>The path an open record's member is named with, as the diagnostics name an undeclared member.</summary>
    public const string OpenMemberPath = "$.*";

    /// <summary>Walks a schema and answers every position at which the record's contract admits a string.</summary>
    /// <param name="schemaRoot">The schema document, as the package holds it.</param>
    /// <param name="boundBytes">The row bound the value must occupy exactly.</param>
    /// <param name="confirm">Confirms a built value against the delivered schema check, or null to leave it unconfirmed.</param>
    /// <returns>The positions, in a depth-first order of the contract's declaration.</returns>
    /// <exception cref="AtBoundWalkException">The contract reaches a shape the walk cannot read, or the record root is absent.</exception>
    public static IReadOnlyList<AtBoundPosition> Walk(
        JsonNode schemaRoot,
        int boundBytes,
        Func<AtBoundPosition, string, bool>? confirm = null)
    {
        ArgumentNullException.ThrowIfNull(schemaRoot);
        var walker = new Walker(schemaRoot, boundBytes, confirm);
        return walker.Run();
    }

    /// <summary>The formats that cannot hold the bound at any value.</summary>
    private static readonly HashSet<string> BoundedFormats =
        new(StringComparer.Ordinal) { "uuid", "date", "time", "date-time", "ipv4", "ipv6", "email", "uri" };

    /// <summary>The format that can hold a long value.</summary>
    private static readonly HashSet<string> LongValueFormats =
        new(StringComparer.Ordinal) { "duration" };

    private static readonly Regex PlainName = new("^[A-Za-z_][A-Za-z0-9_-]{0,127}$", RegexOptions.CultureInvariant);

    /// <summary>One walk over one schema: the positions, the caps a <c>not</c> declares and the depth.</summary>
    private sealed class Walker(JsonNode root, int bound, Func<AtBoundPosition, string, bool>? confirm)
    {
        private readonly List<AtBoundPosition> _positions = [];
        private readonly List<(string Path, string Cap)> _notCaps = [];
        private int _order;

        public IReadOnlyList<AtBoundPosition> Run()
        {
            var record = ResolvePointer("/properties/items/items")
                ?? throw new AtBoundWalkException(
                    "record-root",
                    "the schema declares no record subschema at #/properties/items/items.");

            WalkNode(record, [], "$", depth: 1, AtBoundPositionShape.RootMember, isRecordRoot: true, [], Limits.Empty);
            ApplyNotCaps();

            if (confirm is not null)
            {
                for (var index = 0; index < _positions.Count; index++)
                {
                    var position = _positions[index];
                    if (position.Cap is null && position.Undecided is null && position.Admits is null
                        && position.Value is { } value)
                    {
                        var confirmed = confirm(position, value);
                        _positions[index] = position with
                        {
                            Admits = confirmed ? true : null,
                            Undecided = confirmed ? null : "schema",
                        };
                    }
                }
            }

            return _positions;
        }

        private void WalkNode(
            JsonNode? schema,
            IReadOnlyList<AtBoundStep> steps,
            string path,
            int depth,
            AtBoundPositionShape shape,
            bool isRecordRoot,
            IReadOnlyList<AtBoundCondition> conditions,
            Limits inherited)
        {
            if (depth > DepthLimit)
            {
                throw new AtBoundWalkException(
                    "depth",
                    "the record's contract nests deeper than the profile's depth ceiling.");
            }

            var node = Resolve(schema);
            if (node is not JsonObject obj || IsBooleanFalse(obj))
            {
                return;
            }

            var own = CollectLimits(obj);
            var all = Merge(inherited, own);
            all = MergeWithBranches(all, obj);

            if (!isRecordRoot)
            {
                var effectiveShape = all.FromAllOfOnly && !own.AdmitsString
                    ? AtBoundPositionShape.AllOfBranch
                    : shape;
                if (IsNullableString(obj))
                {
                    effectiveShape = AtBoundPositionShape.UnionBranch;
                }

                // A union's own node asserts nothing a value must satisfy as a whole: the
                // branch that admits the string is the position, and the branch carries its
                // own constraints. Adding the node's own position would measure the union
                // twice, once without the branch's readings.
                var union = obj["anyOf"] is JsonArray || obj["oneOf"] is JsonArray;
                if (all.AdmitsString && !union)
                {
                    AddPosition(steps, path, effectiveShape, all, conditions);
                }
            }

            Traverse(obj, steps, path, depth, shape, conditions);
        }

        private void Traverse(
            JsonObject obj,
            IReadOnlyList<AtBoundStep> steps,
            string path,
            int depth,
            AtBoundPositionShape shape,
            IReadOnlyList<AtBoundCondition> conditions)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal);
            if (obj["properties"] is JsonObject properties)
            {
                foreach (var (name, memberSchema) in properties)
                {
                    _ = declared.Add(name);
                    WalkNode(
                        memberSchema,
                        [.. steps, AtBoundStep.Member(name)],
                        path + MemberPath(name),
                        depth + 1,
                        MemberShape(shape, steps),
                        isRecordRoot: false,
                        conditions,
                        Limits.Empty);
                }
            }

            var maxItems = Number(obj, "maxItems");
            if (maxItems != 0)
            {
                if (obj["prefixItems"] is JsonArray prefix)
                {
                    for (var index = 0; index < prefix.Count; index++)
                    {
                        WalkNode(
                            prefix[index],
                            [.. steps, AtBoundStep.Element(index)],
                            path + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]",
                            depth + 1,
                            AtBoundPositionShape.TupleElement,
                            isRecordRoot: false,
                            conditions,
                            Limits.Empty);
                    }

                    if (obj["items"] is JsonNode pastTuple)
                    {
                        WalkNode(
                            pastTuple,
                            [.. steps, AtBoundStep.Element(prefix.Count)],
                            path + "[" + prefix.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]",
                            depth + 1,
                            AtBoundPositionShape.ArrayElement,
                            isRecordRoot: false,
                            conditions,
                            Limits.Empty);
                    }
                }
                else if (obj["items"] is JsonNode items && !IsBooleanFalse(items))
                {
                    WalkNode(
                        items,
                        [.. steps, AtBoundStep.Element(0)],
                        path + "[0]",
                        depth + 1,
                        AtBoundPositionShape.ArrayElement,
                        isRecordRoot: false,
                        conditions,
                        Limits.Empty);
                }
            }

            var patternProperties = obj["patternProperties"] as JsonObject;
            if (patternProperties is not null)
            {
                foreach (var (pattern, memberSchema) in patternProperties)
                {
                    var name = NameFor(pattern, declared);
                    WalkOpen(
                        memberSchema,
                        name,
                        steps,
                        path,
                        depth,
                        AtBoundPositionShape.OpenPattern,
                        conditions,
                        name is null ? "pattern-name" : null);
                }
            }

            foreach (var (keyword, openShape) in new[]
            {
                ("additionalProperties", AtBoundPositionShape.OpenAdditional),
                ("unevaluatedProperties", AtBoundPositionShape.OpenUnevaluated),
            })
            {
                if (obj[keyword] is JsonObject openSchema)
                {
                    var name = OpenName(declared, obj);
                    WalkOpen(openSchema, name, steps, path, depth, openShape, conditions, name is null ? "open-name" : null);
                }
                else if (obj[keyword] is JsonValue boolean && boolean.GetValueKind() == JsonValueKind.True)
                {
                    var name = OpenName(declared, obj);
                    WalkOpen(null, name, steps, path, depth, openShape, conditions, name is null ? "open-name" : null);
                }
            }

            if (obj["allOf"] is JsonArray allOf)
            {
                foreach (var branch in allOf)
                {
                    WalkNode(
                        branch,
                        steps,
                        path,
                        depth + 1,
                        AtBoundPositionShape.AllOfBranch,
                        isRecordRoot: true,
                        conditions,
                        Limits.Empty);
                }
            }

            WalkBranches(obj, "anyOf", AtBoundPositionShape.UnionBranch, steps, path, depth, conditions);
            WalkBranches(obj, "oneOf", AtBoundPositionShape.UnionBranch, steps, path, depth, conditions);

            if (obj["then"] is JsonNode then)
            {
                WalkNode(then, steps, path, depth + 1, AtBoundPositionShape.ConditionalThen, isRecordRoot: true,
                    [.. conditions, .. ConditionsOf(obj, satisfy: true)], Limits.Empty);
            }

            if (obj["else"] is JsonNode otherwise)
            {
                WalkNode(otherwise, steps, path, depth + 1, AtBoundPositionShape.ConditionalElse, isRecordRoot: true,
                    [.. conditions, .. ConditionsOf(obj, satisfy: false)], Limits.Empty);
            }

            if (obj["dependentSchemas"] is JsonObject dependent)
            {
                foreach (var (trigger, dependentSchema) in dependent)
                {
                    var value = TriggerValue(obj, trigger);
                    IReadOnlyList<AtBoundCondition> withTrigger = value is null
                        ? conditions
                        : [.. conditions, new AtBoundCondition(trigger, value)];
                    WalkNode(
                        dependentSchema,
                        steps,
                        path,
                        depth + 1,
                        AtBoundPositionShape.DependentSchema,
                        isRecordRoot: true,
                        withTrigger,
                        Limits.Empty);
                }
            }

            CollectNotCaps(obj, path);
        }

        private void WalkOpen(
            JsonNode? memberSchema,
            string? name,
            IReadOnlyList<AtBoundStep> steps,
            string path,
            int depth,
            AtBoundPositionShape shape,
            IReadOnlyList<AtBoundCondition> conditions,
            string? undecided)
        {
            var resolved = memberSchema is null ? null : Resolve(memberSchema) as JsonObject;
            var limits = resolved is null ? Limits.Empty : Merge(Limits.Empty, CollectLimits(resolved));
            IReadOnlyList<AtBoundStep> stepsToPosition = [.. steps, AtBoundStep.Member(name ?? "a")];
            var positionPath = path + ".*";
            if (undecided is not null)
            {
                _positions.Add(new AtBoundPosition
                {
                    Path = positionPath,
                    Shape = shape,
                    Steps = stepsToPosition,
                    Order = _order++,
                    Undecided = undecided,
                    Conditions = conditions,
                });
                return;
            }

            if (!limits.AdmitsString)
            {
                return;
            }

            AddPosition(stepsToPosition, positionPath, shape, limits, conditions);

            if (resolved is not null)
            {
                Traverse(resolved, stepsToPosition, positionPath, depth + 1, shape, conditions);
            }
        }

        private void WalkBranches(
            JsonObject obj,
            string keyword,
            AtBoundPositionShape shape,
            IReadOnlyList<AtBoundStep> steps,
            string path,
            int depth,
            IReadOnlyList<AtBoundCondition> conditions)
        {
            if (obj[keyword] is not JsonArray branches)
            {
                return;
            }

            foreach (var branch in branches)
            {
                WalkNode(branch, steps, path, depth + 1, shape, isRecordRoot: false, conditions, Limits.Empty);
            }
        }

        private void AddPosition(
            IReadOnlyList<AtBoundStep> steps,
            string path,
            AtBoundPositionShape shape,
            Limits limits,
            IReadOnlyList<AtBoundCondition> conditions)
        {
            var (cap, undecided, value) = ReadPosition(limits);
            _positions.Add(new AtBoundPosition
            {
                Path = path,
                Shape = shape,
                Steps = steps,
                Order = _order++,
                Cap = cap,
                Undecided = undecided,
                Value = value,
                Conditions = conditions,
            });
        }

        private (string? Cap, string? Undecided, string? Value) ReadPosition(Limits limits)
        {
            if (limits.MaxLength is { } maximum && maximum < bound)
            {
                return ("maxLength " + maximum.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null);
            }

            if (limits.MinLength is { } minimum && minimum > bound)
            {
                return ("minLength " + minimum.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null);
            }

            foreach (var format in limits.Formats)
            {
                if (BoundedFormats.Contains(format))
                {
                    return ("format " + format, null, null);
                }
            }

            foreach (var format in limits.Formats)
            {
                if (!LongValueFormats.Contains(format))
                {
                    return (null, "format " + format, null);
                }
            }

            if (limits.ExactImpossible)
            {
                return ("const-enum", null, null);
            }

            if (limits.Exact.Count > 0)
            {
                foreach (var candidate in limits.Exact)
                {
                    if (Encoding.UTF8.GetByteCount(candidate) == bound)
                    {
                        return (null, null, candidate);
                    }
                }

                return ("const-enum", null, null);
            }

            if (limits.Patterns.Count > 0)
            {
                var value = AtBoundText.BuildForPattern(limits.Patterns[0], bound);
                return value is null ? (null, "pattern", null) : (null, null, value);
            }

            if (limits.Formats.Count > 0)
            {
                foreach (var format in limits.Formats)
                {
                    if (AtBoundText.BuildForFormat(format, bound) is { } value)
                    {
                        return (null, null, value);
                    }
                }

                return (null, "format", null);
            }

            try
            {
                return (null, null, AtBoundText.Build(bound));
            }
            catch (AtBoundValueException)
            {
                throw new AtBoundWalkException(
                    "bound",
                    "the builder cannot produce a plain value of exactly the bound this walk was given.");
            }
        }

        private void CollectNotCaps(JsonObject obj, string path)
        {
            if (obj["not"] is not JsonObject forbidden)
            {
                if (obj["not"] is JsonValue value && value.GetValueKind() == JsonValueKind.True)
                {
                    _notCaps.Add((path, "not"));
                }

                return;
            }

            if (forbidden["properties"] is not JsonObject properties)
            {
                return;
            }

            foreach (var (name, subschema) in properties)
            {
                var resolved = Resolve(subschema) as JsonObject;
                var limits = resolved is null ? Limits.Empty : CollectLimits(resolved);
                var (cap, _, _) = ReadPosition(limits);
                if (cap is null)
                {
                    // The `not` forbids a value the contract may admit at the bound: the path
                    // cannot carry the at-bound value, so it is capped by the `not`.
                    _notCaps.Add((path + MemberPath(name), "not"));
                }
            }
        }

        private void ApplyNotCaps()
        {
            if (_notCaps.Count == 0)
            {
                return;
            }

            for (var index = 0; index < _positions.Count; index++)
            {
                var position = _positions[index];
                foreach (var (capPath, cap) in _notCaps)
                {
                    if (string.Equals(position.Path, capPath, StringComparison.Ordinal)
                        || position.Path.StartsWith(capPath + ".", StringComparison.Ordinal)
                        || position.Path.StartsWith(capPath + "[", StringComparison.Ordinal))
                    {
                        _positions[index] = position with { Cap = cap, Value = null, Admits = null };
                        break;
                    }
                }
            }
        }

        private Limits CollectLimits(JsonObject obj)
        {
            var limits = Limits.Empty;
            if (obj["type"] is JsonNode type)
            {
                limits = limits with { NotString = !TypeAdmitsString(type) };
            }

            if (obj["const"] is JsonNode constant)
            {
                if (constant.GetValueKind() == JsonValueKind.String && constant.GetValue<string>() is { } text)
                {
                    limits = limits with { Exact = [.. limits.Exact, text], ExactConstrained = true };
                }
                else
                {
                    limits = limits with { NotString = true };
                }
            }

            if (obj["enum"] is JsonArray values)
            {
                var strings = values
                    .Where(value => value is not null && value.GetValueKind() == JsonValueKind.String)
                    .Select(value => value!.GetValue<string>())
                    .ToList();
                if (strings.Count == 0)
                {
                    limits = limits with { NotString = true };
                }
                else if (limits.ExactConstrained)
                {
                    var intersection = limits.Exact.Intersect(strings, StringComparer.Ordinal).ToList();
                    limits = limits with
                    {
                        Exact = intersection,
                        ExactImpossible = limits.ExactImpossible || intersection.Count == 0,
                    };
                }
                else
                {
                    limits = limits with { Exact = strings, ExactConstrained = true };
                }
            }

            if (Number(obj, "maxLength") is { } maximum)
            {
                limits = limits with { MaxLength = limits.MaxLength is { } current ? Math.Min(current, maximum) : maximum };
            }

            if (Number(obj, "minLength") is { } minimum)
            {
                limits = limits with { MinLength = limits.MinLength is { } current ? Math.Max(current, minimum) : minimum };
            }

            if (obj["pattern"] is JsonValue pattern && pattern.GetValueKind() == JsonValueKind.String)
            {
                limits = limits with { Patterns = [.. limits.Patterns, pattern.GetValue<string>()] };
            }

            if (obj["format"] is JsonValue format && format.GetValueKind() == JsonValueKind.String)
            {
                limits = limits with { Formats = [.. limits.Formats, format.GetValue<string>()] };
            }

            return limits;
        }

        private Limits MergeWithBranches(Limits limits, JsonObject obj)
        {
            if (obj["allOf"] is not JsonArray branches)
            {
                return limits;
            }

            var any = false;
            var merged = limits;
            foreach (var branch in branches)
            {
                if (Resolve(branch) is not JsonObject resolved)
                {
                    continue;
                }

                var branchLimits = Merge(CollectLimits(resolved), CollectLimitsOfAllOf(resolved));
                any |= branchLimits.AdmitsString;
                merged = Merge(merged, branchLimits);
            }

            return any && !limits.AdmitsString ? merged with { FromAllOfOnly = true } : merged;
        }

        private Limits CollectLimitsOfAllOf(JsonObject obj)
        {
            var merged = Limits.Empty;
            if (obj["allOf"] is JsonArray branches)
            {
                foreach (var branch in branches)
                {
                    if (Resolve(branch) is JsonObject resolved)
                    {
                        merged = Merge(merged, Merge(CollectLimits(resolved), CollectLimitsOfAllOf(resolved)));
                    }
                }
            }

            return merged;
        }

        private static Limits Merge(Limits left, Limits right)
        {
            var exact = left.Exact;
            var constrained = left.ExactConstrained || right.ExactConstrained;
            var impossible = left.ExactImpossible || right.ExactImpossible;

            if (left.ExactConstrained && right.ExactConstrained)
            {
                exact = left.Exact.Intersect(right.Exact, StringComparer.Ordinal).ToList();
                if (exact.Count == 0)
                {
                    impossible = true;
                }
            }
            else if (right.ExactConstrained)
            {
                exact = right.Exact;
            }

            return new Limits
            {
                NotString = left.NotString || right.NotString,
                MaxLength = Min(left.MaxLength, right.MaxLength),
                MinLength = Max(left.MinLength, right.MinLength),
                Patterns = [.. left.Patterns.Union(right.Patterns, StringComparer.Ordinal)],
                Formats = [.. left.Formats.Union(right.Formats, StringComparer.Ordinal)],
                Exact = exact,
                ExactConstrained = constrained,
                ExactImpossible = impossible,
                FromAllOfOnly = left.FromAllOfOnly || right.FromAllOfOnly,
            };
        }

        private static int? Min(int? left, int? right) =>
            left is null ? right : right is null ? left : Math.Min(left.Value, right.Value);

        private static int? Max(int? left, int? right) =>
            left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);

        private static bool TypeAdmitsString(JsonNode type)
        {
            if (type.GetValueKind() == JsonValueKind.String)
            {
                return string.Equals(type.GetValue<string>(), "string", StringComparison.Ordinal);
            }

            if (type is JsonArray values)
            {
                return values.Any(value => value is not null
                    && value.GetValueKind() == JsonValueKind.String
                    && string.Equals(value.GetValue<string>(), "string", StringComparison.Ordinal));
            }

            return false;
        }

        private static bool IsNullableString(JsonObject obj) =>
            obj["type"] is JsonArray types
            && types.Any(item => item is not null
                && item.GetValueKind() == JsonValueKind.String
                && string.Equals(item.GetValue<string>(), "string", StringComparison.Ordinal))
            && types.Any(item => item is not null
                && item.GetValueKind() == JsonValueKind.String
                && string.Equals(item.GetValue<string>(), "null", StringComparison.Ordinal));

        private static AtBoundPositionShape MemberShape(AtBoundPositionShape shape, IReadOnlyList<AtBoundStep> steps) =>
            shape is AtBoundPositionShape.AllOfBranch
                or AtBoundPositionShape.ConditionalThen
                or AtBoundPositionShape.ConditionalElse
                or AtBoundPositionShape.DependentSchema
                or AtBoundPositionShape.UnionBranch
                ? shape
                : steps.Count == 0
                    ? AtBoundPositionShape.RootMember
                    : steps[^1].MemberName is not null
                        ? AtBoundPositionShape.NestedMember
                        : AtBoundPositionShape.ArrayElementMember;

        private JsonNode? TriggerValue(JsonObject recordSchema, string trigger)
        {
            if (recordSchema["properties"] is JsonObject properties
                && properties[trigger] is JsonNode declared
                && Resolve(declared) is JsonObject schema)
            {
                return ValueFor(schema);
            }

            return JsonValue.Create("x");
        }

        private static JsonNode? ValueFor(JsonObject schema)
        {
            if (schema["const"] is JsonNode constant)
            {
                return constant.DeepClone();
            }

            if (schema["enum"] is JsonArray values && values.Count > 0)
            {
                return values[0]?.DeepClone();
            }

            var type = schema["type"];
            if (type is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            {
                return value.GetValue<string>() switch
                {
                    "integer" => JsonValue.Create(0L),
                    "number" => JsonValue.Create(0m),
                    "boolean" => JsonValue.Create(false),
                    "array" => new JsonArray(),
                    "object" => new JsonObject(),
                    null => null,
                    _ => JsonValue.Create("x"),
                };
            }

            if (type is JsonArray many && many.Any(item => item is not null
                && item.GetValueKind() == JsonValueKind.String
                && string.Equals(item.GetValue<string>(), "object", StringComparison.Ordinal)))
            {
                return new JsonObject();
            }

            return JsonValue.Create("x");
        }

        private IReadOnlyList<AtBoundCondition> ConditionsOf(JsonObject obj, bool satisfy)
        {
            if (obj["if"] is not JsonObject ifSchema || ifSchema["properties"] is not JsonObject properties)
            {
                return [];
            }

            var conditions = new List<AtBoundCondition>();
            foreach (var (name, subschema) in properties)
            {
                if (Resolve(subschema) is not JsonObject resolved)
                {
                    continue;
                }

                if (resolved["const"] is JsonValue constant && constant.GetValueKind() == JsonValueKind.String)
                {
                    var value = constant.GetValue<string>();
                    conditions.Add(new AtBoundCondition(name, JsonValue.Create(satisfy ? value : value + "x")));
                }
                else if (satisfy)
                {
                    conditions.Add(new AtBoundCondition(name, ValueFor(resolved)));
                }
            }

            return conditions;
        }

        private string? NameFor(string pattern, IReadOnlySet<string> declared)
        {
            var built = AtBoundText.BuildForPattern(pattern, 8);
            if (built is not null && !declared.Contains(built) && Matches(pattern, built))
            {
                return built;
            }

            foreach (var candidate in new[] { "a", "x", "extra", "0", "abc" })
            {
                if (!declared.Contains(candidate) && Matches(pattern, candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool Matches(string pattern, string value)
        {
            try
            {
                return Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private string? OpenName(IReadOnlySet<string> declared, JsonObject obj)
        {
            var propertyNames = obj["propertyNames"] as JsonObject;
            var patterns = obj["patternProperties"] as JsonObject;
            foreach (var candidate in new[] { "extra", "extra1", "note", "value" })
            {
                if (declared.Contains(candidate))
                {
                    continue;
                }

                if (propertyNames is not null && !PropertyNameAdmitted(propertyNames, candidate))
                {
                    continue;
                }

                if (patterns is not null && patterns.Any(pattern => Matches(pattern.Key, candidate)))
                {
                    // A patterned member is already declared by the contract; the open member
                    // must be one no evaluated keyword declares.
                    continue;
                }

                return candidate;
            }

            return null;
        }

        private static bool PropertyNameAdmitted(JsonObject propertyNames, string candidate)
        {
            if (Number(propertyNames, "maxLength") is { } maximum && candidate.Length > maximum)
            {
                return false;
            }

            if (Number(propertyNames, "minLength") is { } minimum && candidate.Length < minimum)
            {
                return false;
            }

            return propertyNames["pattern"] is not JsonValue pattern
                || pattern.GetValueKind() != JsonValueKind.String
                || Matches(pattern.GetValue<string>(), candidate);
        }

        private JsonNode? Resolve(JsonNode? schema)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var node = schema;
            while (node is JsonObject obj
                && obj["$ref"] is JsonValue reference
                && reference.GetValueKind() == JsonValueKind.String
                && reference.GetValue<string>() is { } pointer)
            {
                if (!seen.Add(pointer))
                {
                    throw new AtBoundWalkException("cycle", "the record's contract references itself without descending into the value.");
                }

                if (!pointer.StartsWith("#/", StringComparison.Ordinal))
                {
                    throw new AtBoundWalkException("reference", "the record's contract uses a reference that is not a fragment of its own schema.");
                }

                node = ResolvePointer(pointer[1..])
                    ?? throw new AtBoundWalkException("reference", "the record's contract references a position its schema does not hold.");
            }

            return node;
        }

        private JsonNode? ResolveStatic(JsonNode? schema)
        {
            var node = schema;
            var guard = 0;
            while (node is JsonObject obj
                && obj["$ref"] is JsonValue reference
                && reference.GetValueKind() == JsonValueKind.String
                && reference.GetValue<string>() is { } pointer
                && pointer.StartsWith("#/", StringComparison.Ordinal))
            {
                if (++guard > DepthLimit)
                {
                    return null;
                }

                node = ResolvePointer(pointer[1..]);
            }

            return node;
        }

        private JsonNode? ResolvePointer(string pointer)
        {
            JsonNode? node = root;
            if (pointer.Length == 0)
            {
                return root;
            }

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

        private static int? Number(JsonObject obj, string member) =>
            obj[member] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

        private static bool IsBooleanFalse(JsonNode node) =>
            node is JsonValue value && value.GetValueKind() == JsonValueKind.False;

        private static string MemberPath(string name) =>
            PlainName.IsMatch(name)
                ? "." + name
                : "['" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "']";
    }

    /// <summary>The constraints one path's own schemas impose on a string value.</summary>
    private sealed record Limits
    {
        public static Limits Empty { get; } = new();

        public bool NotString { get; init; }

        public int? MaxLength { get; init; }

        public int? MinLength { get; init; }

        public IReadOnlyList<string> Patterns { get; init; } = [];

        public IReadOnlyList<string> Formats { get; init; } = [];

        public IReadOnlyList<string> Exact { get; init; } = [];

        public bool ExactConstrained { get; init; }

        public bool ExactImpossible { get; init; }

        public bool FromAllOfOnly { get; init; }

        public bool AdmitsString => !NotString;
    }
}
