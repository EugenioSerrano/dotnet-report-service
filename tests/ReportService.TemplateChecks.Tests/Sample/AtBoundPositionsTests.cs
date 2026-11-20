using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReportService.Data;
using ReportService.DataContract;
using ReportService.TemplateChecks.Sample;
using ReportService.Testing.TemplatePackages;

namespace ReportService.TemplateChecks.Tests.Sample;

/// <summary>
/// The position walk over the record's contract, one case per shape the data-contract
/// dialect admits: the record's own member, a nested object, an array element and an
/// object element's member, an open record through its additional, patterned and
/// unevaluated members, the string branch of a nullable field, and a position reached
/// only through <c>allOf</c>, an <c>if</c> branch, a <c>dependentSchemas</c> subschema or
/// a tuple - plus the readings of <c>maxLength</c>/<c>minLength</c>, <c>const</c>/<c>enum</c>,
/// <c>pattern</c>, the closed <c>format</c> list - <c>email</c> and <c>uri</c> bounded by
/// the delivered check's own limits, <c>duration</c> the one long-value format - <c>not</c>
/// capping the path it forbids, the depth ceiling and the reference resolution. No shape is
/// left to an argument.
/// </summary>
public sealed class AtBoundPositionsTests
{
    private const int Bound = PdfRowBound.Bytes;

    [Fact]
    public void TheRecordsOwnMemberIsAPosition()
    {
        var position = Single("""{ "type": "object", "properties": { "description": { "type": "string" } } }""", "$.description");

        Assert.Equal(AtBoundPositionShape.RootMember, position.Shape);
        Assert.Null(position.Cap);
        Assert.Null(position.Undecided);
        Assert.Equal(Bound, Encoding.UTF8.GetByteCount(position.Value!));
    }

    [Fact]
    public void ANestedObjectMemberIsAPosition()
    {
        var position = Single(
            """{ "type": "object", "properties": { "detail": { "type": "object", "properties": { "note": { "type": "string" } }, "required": ["note"] } } }""",
            "$.detail.note");

        Assert.Equal(AtBoundPositionShape.NestedMember, position.Shape);
    }

    [Theory]
    [InlineData("""{ "type": "object", "properties": { "tags": { "type": "array", "items": { "type": "string" } } } }""", "$.tags[0]", AtBoundPositionShape.ArrayElement)]
    [InlineData("""{ "type": "object", "properties": { "lines": { "type": "array", "items": { "type": "object", "properties": { "text": { "type": "string" } } } } } }""", "$.lines[0].text", AtBoundPositionShape.ArrayElementMember)]
    [InlineData("""{ "type": "object", "properties": { "tags": { "type": "array", "prefixItems": [{ "type": "string" }], "items": { "type": "string", "maxLength": 5 } } } }""", "$.tags[0]", AtBoundPositionShape.TupleElement)]
    public void AnArrayPositionIsAPosition(string recordSchema, string path, AtBoundPositionShape shape)
    {
        var position = Single(recordSchema, path);

        Assert.Equal(shape, position.Shape);
        Assert.Null(position.Cap);
    }

    [Fact]
    public void AnIndexPastTheTupleFallsToItems_AndIsCappedByItsOwnMaximum()
    {
        var positions = Walk("""{ "type": "object", "properties": { "tags": { "type": "array", "prefixItems": [{ "type": "string" }], "items": { "type": "string", "maxLength": 5 } } } }""");

        var past = Single(positions, "$.tags[1]");
        Assert.Equal(AtBoundPositionShape.ArrayElement, past.Shape);
        Assert.Equal("maxLength 5", past.Cap);
    }

    [Theory]
    [InlineData("""{ "type": "object", "properties": { "sequence": { "type": "integer" } }, "additionalProperties": { "type": "string" } }""", AtBoundPositionShape.OpenAdditional)]
    [InlineData("""{ "type": "object", "properties": { "sequence": { "type": "integer" } }, "patternProperties": { "^x-[a-z]+$": { "type": "string" } } }""", AtBoundPositionShape.OpenPattern)]
    [InlineData("""{ "type": "object", "properties": { "sequence": { "type": "integer" } }, "unevaluatedProperties": { "type": "string" } }""", AtBoundPositionShape.OpenUnevaluated)]
    public void AnOpenRecordsUndeclaredMemberIsAPosition(string recordSchema, AtBoundPositionShape shape)
    {
        var walked = Walk(recordSchema);
        var position = Single(walked, "$.*");
        Assert.Equal(shape, position.Shape);
        Assert.Null(position.Cap);
        Assert.NotNull(position.Steps[0].MemberName);
    }

    [Fact]
    public void TheStringBranchOfANullableFieldIsAPosition_AndNullIsNot()
    {
        var position = Single(
            """{ "type": "object", "properties": { "note": { "type": ["string", "null"] } } }""",
            "$.note");

        Assert.Equal(AtBoundPositionShape.UnionBranch, position.Shape);
        Assert.NotNull(position.Value);
    }

    [Fact]
    public void TheStringBranchOfAUnionIsAPosition()
    {
        var positions = Walk("""{ "type": "object", "properties": { "note": { "anyOf": [{ "type": "null" }, { "type": "string" }] } } }""");

        var position = Single(positions, "$.note");
        Assert.Equal(AtBoundPositionShape.UnionBranch, position.Shape);
        Assert.NotNull(position.Value);
    }

    [Fact]
    public void APositionReachedOnlyThroughAllOfIsAPosition()
    {
        var position = Single(
            """{ "type": "object", "properties": { "sequence": { "type": "integer" } }, "allOf": [{ "properties": { "description": { "type": "string" } } }] }""",
            "$.description");

        Assert.Equal(AtBoundPositionShape.AllOfBranch, position.Shape);
        Assert.Null(position.Cap);
    }

    [Fact]
    public void APositionReachedOnlyThroughTheThenAndElseBranchesIsAPosition()
    {
        const string Record = """
            {
              "type": "object",
              "properties": { "mode": { "type": "string" }, "amount": { "type": "number" } },
              "required": ["mode"],
              "if": { "properties": { "mode": { "const": "detailed" } }, "required": ["mode"] },
              "then": { "properties": { "description": { "type": "string" } } },
              "else": { "properties": { "note": { "type": "string" } } }
            }
            """;

        var positions = Walk(Record);

        var then = Single(positions, "$.description");
        var otherwise = Single(positions, "$.note");
        Assert.Equal(AtBoundPositionShape.ConditionalThen, then.Shape);
        Assert.Equal(AtBoundPositionShape.ConditionalElse, otherwise.Shape);
        Assert.Contains(then.Conditions, condition => condition.Member == "mode" && condition.Value!.GetValue<string>() == "detailed");
        Assert.Contains(otherwise.Conditions, condition => condition.Member == "mode" && condition.Value!.GetValue<string>() != "detailed");
    }

    [Fact]
    public void APositionDeclaredOnlyInADependentSchemaIsAPosition_WithItsTrigger()
    {
        const string Record = """
            {
              "type": "object",
              "properties": { "kind": { "type": "string" }, "sequence": { "type": "integer" } },
              "dependentSchemas": { "kind": { "properties": { "note": { "type": "string" } } } }
            }
            """;

        var position = Single(Record, "$.note");

        Assert.Equal(AtBoundPositionShape.DependentSchema, position.Shape);
        Assert.Contains(position.Conditions, condition => condition.Member == "kind");
    }

    [Fact]
    public void NotContributesNoPositionAndCapsThePathItForbids()
    {
        const string Record = """
            {
              "type": "object",
              "properties": { "description": { "type": "string" } },
              "not": { "properties": { "description": { "type": "string" } }, "required": ["description"] }
            }
            """;

        var positions = Walk(Record);

        var position = Single(positions, "$.description");
        Assert.Equal("not", position.Cap);
        Assert.Single(positions);
    }

    [Theory]
    [InlineData("""{ "type": "object", "properties": { "description": { "type": "string", "maxLength": 65536 } } }""", "maxLength 65536")]
    [InlineData("""{ "type": "object", "properties": { "description": { "type": "string", "minLength": 262145 } } }""", "minLength 262145")]
    public void ALengthBoundBelowOrAboveTheBoundDoesNotAdmit(string recordSchema, string cap)
    {
        Assert.Equal(cap, Single(recordSchema, "$.description").Cap);
    }

    [Fact]
    public void AConstOrEnumAdmitsOnlyThroughAMemberOfExactlyTheBound()
    {
        var exact = new string('a', Bound);
        var admits = Single(
            $$"""{ "type": "object", "properties": { "code": { "type": "string", "enum": ["short", "{{exact}}"] } } }""",
            "$.code");
        Assert.Null(admits.Cap);
        Assert.Equal(Bound, Encoding.UTF8.GetByteCount(admits.Value!));

        var capped = Single(
            """{ "type": "object", "properties": { "code": { "type": "string", "enum": ["short", "shorter"] } } }""",
            "$.code");
        Assert.Equal("const-enum", capped.Cap);
    }

    [Theory]
    [InlineData("uuid")]
    [InlineData("date")]
    [InlineData("time")]
    [InlineData("date-time")]
    [InlineData("ipv4")]
    [InlineData("ipv6")]
    [InlineData("email")]
    [InlineData("uri")]
    public void ABoundedFormatDoesNotAdmit(string format)
    {
        var position = Single($$"""{ "type": "object", "properties": { "code": { "type": "string", "format": "{{format}}" } } }""", "$.code");

        Assert.Equal("format " + format, position.Cap);
    }

    [Fact]
    public void TheDeliveredSchemaCheckBoundsAnEmailAndAUriBelowTheRowBound()
    {
        // The walk reads email and uri as bounded because the delivered schema check
        // cannot hold a value at the bound for either: an e-mail local part of 64
        // characters is valid and one of 65 is refused, a URI of 32 768 bytes is valid
        // and one of 65 534 is refused, and a value of exactly 262 144 bytes is refused
        // for both. The case keeps the classification measured, so a check that widened
        // either format would fail here instead of silently falsifying the reading.
        const string MailDomain = "@example.com";
        const string UriPrefix = "https://example.com/";

        Assert.True(TheDeliveredCheckAdmits("email", new string('a', 64) + MailDomain));
        Assert.False(TheDeliveredCheckAdmits("email", new string('a', 65) + MailDomain));
        Assert.False(TheDeliveredCheckAdmits("email", new string('a', Bound - MailDomain.Length) + MailDomain));

        Assert.True(TheDeliveredCheckAdmits("uri", UriPrefix + new string('a', 32_768 - UriPrefix.Length)));
        Assert.False(TheDeliveredCheckAdmits("uri", UriPrefix + new string('a', 65_534 - UriPrefix.Length)));
        Assert.False(TheDeliveredCheckAdmits("uri", UriPrefix + new string('a', Bound - UriPrefix.Length)));
    }

    [Theory]
    [InlineData("duration")]
    public void ALongValueFormatIsBuiltAtTheBoundAndConfirmedByTheGivenCheck(string format)
    {
        var position = Single(
            $$"""{ "type": "object", "properties": { "code": { "type": "string", "format": "{{format}}" } } }""",
            "$.code",
            (_, value) => Encoding.UTF8.GetByteCount(value) == Bound);

        Assert.Null(position.Cap);
        Assert.Null(position.Undecided);
        Assert.True(position.Admits);
        Assert.Equal(Bound, Encoding.UTF8.GetByteCount(position.Value!));
    }

    [Fact]
    public void AFormatOutsideTheClosedListIsUndecided()
    {
        var position = Single("""{ "type": "object", "properties": { "code": { "type": "string", "format": "ssn" } } }""", "$.code");

        Assert.Equal("format ssn", position.Undecided);
        Assert.False(position.AdmitsTheBound);
    }

    [Fact]
    public void APatternThatAdmitsTheBoundRuns_AndOneThatCannotIsUndecided()
    {
        var admits = Single(
            """{ "type": "object", "properties": { "code": { "type": "string", "pattern": "^[0-9][0-9 ]*$" } } }""",
            "$.code",
            (_, value) => System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9][0-9 ]*$"));
        Assert.Null(admits.Cap);
        Assert.True(admits.Admits);

        var undecided = Single(
            """{ "type": "object", "properties": { "code": { "type": "string", "pattern": "^[0-9]{4}$" } } }""",
            "$.code");
        Assert.Equal("pattern", undecided.Undecided);
    }

    [Fact]
    public void AReferenceIsResolved()
    {
        const string Schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": {
                "header": { "type": "object" },
                "items": { "type": "array", "items": { "$ref": "#/$defs/record" } }
              },
              "$defs": {
                "record": { "type": "object", "properties": { "record": { "$ref": "#/$defs/item" } } },
                "item": { "type": "object", "properties": { "description": { "type": "string" } } }
              }
            }
            """;
        var positions = AtBoundPositions.Walk(Parse(Schema), Bound, (_, _) => true);

        Assert.Contains(positions, position => position.Path == "$.record.description");
    }

    [Fact]
    public void TheWalkStopsAtTheDepthCeiling()
    {
        var deep = new StringBuilder("""{ "type": "object", "properties": { "a": """);
        for (var level = 0; level < 70; level++)
        {
            _ = deep.Append("""{ "type": "object", "properties": { "a": """);
        }

        _ = deep.Append("""{ "type": "string" }""");
        for (var level = 0; level < 70; level++)
        {
            _ = deep.Append(" } }");
        }

        _ = deep.Append(" } }");

        var raised = Assert.Throws<AtBoundWalkException>(() =>
            AtBoundPositions.Walk(Parse(AtBoundFixtures.Schema(deep.ToString()), depth: 300), Bound));
        Assert.Equal("depth", raised.Code);
    }

    [Fact]
    public void ARecordWhoseContractDeclaresNoStringPositionWalksToNoPosition()
    {
        var positions = Walk("""{ "type": "object", "properties": { "sequence": { "type": "integer" } } }""");

        Assert.Empty(positions);
    }

    private static AtBoundPosition Single(
        string recordSchema,
        string path,
        Func<AtBoundPosition, string, bool>? confirm = null) =>
        Single(Walk(recordSchema, confirm), path);

    private static AtBoundPosition Single(IReadOnlyList<AtBoundPosition> positions, string path) =>
        Assert.Single(positions, position => position.Path == path);

    private static bool TheDeliveredCheckAdmits(string format, string value)
    {
        var recordSchema = $$"""{ "type": "object", "properties": { "code": { "type": "string", "format": "{{format}}" } } }""";
        using var package = Packages.WritePackage(AtBoundFixtures.Package(recordSchema, records: []));
        var candidate = Packages.ReadCandidate(package.Directory);
        var schemaBytes = File.ReadAllBytes(Path.Combine(package.Directory, "schema.json"));
        var compilation = DataContractCompiler.Compile(schemaBytes, candidate.SourceDescriptorSha256, "schema.json");
        Assert.True(compilation.Profile.IsValid, "the fixture's schema is not inside the profile: " + compilation.Profile.Rule);
        Assert.NotNull(compilation.Contract);
        try
        {
            var record = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>(StringComparer.Ordinal) { ["code"] = value });
            return compilation.Contract!.CheckRecord(record).IsValid;
        }
        finally
        {
            compilation.Contract!.Dispose();
        }
    }

    private static IReadOnlyList<AtBoundPosition> Walk(
        string recordSchema,
        Func<AtBoundPosition, string, bool>? confirm = null) =>
        AtBoundPositions.Walk(
            Parse(AtBoundFixtures.Schema(recordSchema)),
            Bound,
            confirm ?? ((_, _) => true));

    private static JsonNode Parse(string json, int depth = 64) =>
        JsonNode.Parse(json, nodeOptions: null, documentOptions: new JsonDocumentOptions { MaxDepth = depth })!;
}
