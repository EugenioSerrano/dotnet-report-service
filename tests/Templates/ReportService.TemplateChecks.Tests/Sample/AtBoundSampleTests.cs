using System.Text;
using System.Text.Json.Nodes;
using ReportService.Data;
using ReportService.DataContract;
using ReportService.TemplateChecks.Sample;
using ReportService.Testing.TemplatePackages;

namespace ReportService.TemplateChecks.Tests.Sample;

/// <summary>
/// The at-bound snapshot: the value at exactly the bound inside the record, the
/// version's own header kept and one record only, the anchored position chosen over an
/// earlier one, a capped position skipped for the next that admits, every shape of the
/// dialect chosen and substituted, the record synthesised from the contract when the
/// sample carries none, the wire size measured with the contract-synthesised fallback,
/// and the limitations the derivation reports without a value.
/// </summary>
public sealed class AtBoundSampleTests
{
    private const int Bound = PdfRowBound.Bytes;

    private const string Control = AtBoundFixtures.ControlRecordSchema;

    [Fact]
    public void TheControlIsTakenWithTheBoundInItsOwnRecordsField()
    {
        var result = Build(Control);

        Assert.True(result.Applies);
        Assert.Equal(AtBoundDerivation.SampleRecord, result.Derivation);
        Assert.Equal("$.description", result.Chosen!.Path);
        Assert.Equal(AtBoundPositionShape.RootMember, result.Chosen.Shape);
        Assert.True(result.AnchorBound);
        Assert.Equal(AtBoundText.Sha256(AtBoundText.Build(Bound)), result.ValueSha256);
        Assert.True(result.RecordBytes > Bound);
        Assert.True(result.RecordBytes <= AtBoundSample.RecordMaximumBytes);

        var document = JsonNode.Parse(result.Document!)!.AsObject();
        var items = document["items"]!.AsArray();
        Assert.Single(items);
        var description = items[0]!["description"]!.GetValue<string>();
        Assert.Equal(Bound, Encoding.UTF8.GetByteCount(description));
        Assert.Equal("Monthly statement", document["header"]!["title"]!.GetValue<string>());
    }

    [Fact]
    public void AnAnchoredPositionIsChosenOverAnEarlierUnanchoredOne()
    {
        const string Record = """
            { "type": "object", "properties": { "description": { "type": "string" }, "note": { "type": "string" } } }
            """;

        var result = Build(Record, itemAnchor: "$.note");

        Assert.Equal("$.note", result.Chosen!.Path);
        Assert.True(result.AnchorBound);
    }

    [Fact]
    public void ACappedAnchoredPositionFallsThroughToTheNextThatAdmits()
    {
        const string Record = """
            {
              "type": "object",
              "properties": {
                "description": { "type": "string", "maxLength": 10 },
                "note": { "type": "string" }
              }
            }
            """;

        var result = Build(Record, itemAnchor: "$.description");

        Assert.Equal("$.note", result.Chosen!.Path);
        Assert.False(result.AnchorBound);
        Assert.Contains(result.Skipped, position => position.Path == "$.description" && position.Cap == "maxLength 10");
    }

    [Theory]
    [InlineData("uuid")]
    [InlineData("email")]
    [InlineData("uri")]
    public void ABoundedFormatBeforeAnAdmittingPositionIsSkipped(string format)
    {
        var record = $$"""
            {
              "type": "object",
              "properties": {
                "code": { "type": "string", "format": "{{format}}" },
                "note": { "type": "string" }
              }
            }
            """;

        var result = Build(record, itemAnchor: "$.code");

        Assert.Equal("$.note", result.Chosen!.Path);
        Assert.Contains(result.Skipped, position => position.Path == "$.code" && position.Cap == "format " + format);
    }

    [Theory]
    [InlineData("""{ "type": "object", "properties": { "description": { "type": "string", "maxLength": 10 } } }""")]
    [InlineData("""{ "type": "object", "properties": { "code": { "type": "string", "format": "uuid" } } }""")]
    [InlineData("""{ "type": "object", "properties": { "sequence": { "type": "integer" } } }""")]
    public void AContractThatAdmitsTheBoundNowhereIsNotApplicable(string recordSchema)
    {
        var result = Build(recordSchema);

        Assert.False(result.Applies);
        Assert.Null(result.Document);
        Assert.Equal(AtBoundDerivation.None, result.Derivation);
        Assert.NotNull(result.NotApplicable);
    }

    [Theory]
    [InlineData("""{ "type": "object", "properties": { "description": { "type": "string" }, "note": { "type": "string" } } }""", "$.description", null)]
    [InlineData("""{ "type": "object", "properties": { "detail": { "type": "object", "properties": { "note": { "type": "string" } } } } }""", "$.detail.note", null)]
    [InlineData("""{ "type": "object", "properties": { "tags": { "type": "array", "items": { "type": "string" } } } }""", "$.tags[0]", null)]
    [InlineData("""{ "type": "object", "properties": { "lines": { "type": "array", "items": { "type": "object", "properties": { "text": { "type": "string" } } } } } }""", "$.lines[0].text", null)]
    [InlineData("""{ "type": "object", "properties": { "sequence": { "type": "integer" } }, "additionalProperties": { "type": "string" } }""", "$.*", """{ "sequence": 1 }""")]
    [InlineData("""{ "type": "object", "properties": { "sequence": { "type": "integer" } }, "patternProperties": { "^x-[a-z]+$": { "type": "string" } } }""", "$.*", null)]
    [InlineData("""{ "type": "object", "properties": { "sequence": { "type": "integer" } }, "unevaluatedProperties": { "type": "string" } }""", "$.*", """{ "sequence": 1 }""")]
    [InlineData("""{ "type": "object", "properties": { "note": { "type": ["string", "null"] } } }""", "$.note", null)]
    [InlineData("""{ "type": "object", "properties": { "sequence": { "type": "integer" } }, "allOf": [{ "properties": { "description": { "type": "string" } } }] }""", "$.description", null)]
    [InlineData("""{ "type": "object", "properties": { "sequence": { "type": "integer" } }, "dependentSchemas": { "kind": { "properties": { "note": { "type": "string" } } } } }""", "$.note", null)]
    [InlineData("""{ "type": "object", "properties": { "tags": { "type": "array", "prefixItems": [{ "type": "string" }], "items": { "type": "string", "maxLength": 5 } } } }""", "$.tags[0]", null)]
    [InlineData("""{ "type": "object", "properties": { "code": { "type": "string", "pattern": "^[0-9][0-9 ]*$" } } }""", "$.code", null)]
    [InlineData("""{ "type": "object", "properties": { "code": { "type": "string", "format": "duration" } } }""", "$.code", null)]
    public void TheShapeIsChosenAndSubstituted(string recordSchema, string path, string? sample)
    {
        string[]? records = sample is null ? null : [sample];
        var result = Build(recordSchema, records, itemAnchor: "$.code");

        Assert.True(result.Applies);
        Assert.Equal(path, result.Chosen!.Path);
        Assert.True(result.RecordBytes <= AtBoundSample.RecordMaximumBytes);
        var record = JsonNode.Parse(result.Document!)!.AsObject()["items"]!.AsArray()[0]!.AsObject();
        Assert.True(ContainsBound(record), "the record printed does not carry the at-bound value");
    }

    [Fact]
    public void TheDeliveredFormatAssertionCannotHoldTheBoundInAnEmailOrAUri()
    {
        // The delivered schema check asserts the format and cannot hold a value at the
        // bound in either: an e-mail local part of 64 characters is valid and one of 65
        // is refused, and a URI of 32 768 bytes is valid while one of 65 534 is refused.
        // No 262 144-byte value of either format exists, so a position carrying one is
        // capped like a maxLength - the contract admits the bound nowhere, the derivation
        // answers with the verdict and prints nothing, and the version is taken rather
        // than reported as a limitation of the gate.
        var email = Build("""{ "type": "object", "properties": { "code": { "type": "string", "format": "email" } } }""");

        Assert.False(email.Applies);
        Assert.Null(email.Chosen);
        Assert.NotNull(email.NotApplicable);
        Assert.Contains("format email", email.NotApplicable, StringComparison.Ordinal);

        var uri = Build("""{ "type": "object", "properties": { "code": { "type": "string", "format": "uri" } } }""");

        Assert.False(uri.Applies);
        Assert.Null(uri.Chosen);
        Assert.NotNull(uri.NotApplicable);
        Assert.Contains("format uri", uri.NotApplicable, StringComparison.Ordinal);
    }

    [Fact]
    public void AConditionalPositionIsChosenAndItsBranchIsSatisfied()
    {
        const string Record = """
            {
              "type": "object",
              "properties": { "mode": { "type": "string" }, "amount": { "type": "number" } },
              "required": ["mode"],
              "if": { "properties": { "mode": { "const": "detailed" } }, "required": ["mode"] },
              "then": { "properties": { "description": { "type": "string", "maxLength": 5 } } },
              "else": { "properties": { "note": { "type": "string" } } }
            }
            """;

        var result = Build(
            Record,
            records: ["""{ "mode": "detailed", "amount": 1.0 }"""],
            itemAnchor: "$.note");

        Assert.Equal("$.note", result.Chosen!.Path);
        Assert.Equal(AtBoundPositionShape.ConditionalElse, result.Chosen.Shape);
        var record = JsonNode.Parse(result.Document!)!.AsObject()["items"]!.AsArray()[0]!.AsObject();
        Assert.NotEqual("detailed", record["mode"]!.GetValue<string>());
        Assert.True(ContainsBound(record));
    }

    [Fact]
    public void ADeclaredPositionTheRecordDoesNotCarryIsMaterialised()
    {
        const string Record = """
            {
              "type": "object",
              "properties": { "sequence": { "type": "integer" }, "note": { "type": "string" } },
              "required": ["sequence", "note"]
            }
            """;

        var result = Build(Record, records: ["""{ "sequence": 1 }"""], itemAnchor: "$.note");

        Assert.Equal("$.note", result.Chosen!.Path);
        var record = JsonNode.Parse(result.Document!)!.AsObject()["items"]!.AsArray()[0]!.AsObject();
        Assert.Equal(Bound, Encoding.UTF8.GetByteCount(record["note"]!.GetValue<string>()));
    }

    [Fact]
    public void ASampleWithNoRecordIsSynthesisedFromTheContract()
    {
        var result = Build(Control, records: []);

        Assert.True(result.Applies);
        Assert.Equal(AtBoundDerivation.Contract, result.Derivation);
        Assert.Equal("$.description", result.Chosen!.Path);
        var record = JsonNode.Parse(result.Document!)!.AsObject()["items"]!.AsArray()[0]!.AsObject();
        Assert.Equal(1L, record["sequence"]!.GetValue<long>());
        Assert.Equal(Bound, Encoding.UTF8.GetByteCount(record["description"]!.GetValue<string>()));
    }

    [Fact]
    public void TheNoRecordTwinWhoseContractAdmitsNothingIsNotApplicable()
    {
        var result = Build("""{ "type": "object", "properties": { "sequence": { "type": "integer" } } }""", records: []);

        Assert.False(result.Applies);
        Assert.NotNull(result.NotApplicable);
    }

    [Fact]
    public void AnOverLimitSampleDerivedRecordFallsBackToTheContract()
    {
        const string Record = """
            {
              "type": "object",
              "properties": {
                "description": { "type": "string" },
                "a": { "type": "string" },
                "b": { "type": "string" },
                "c": { "type": "string" }
              }
            }
            """;
        var large = new string('x', 300_000);
        var sample = $$"""{ "description": "Consulting", "a": "{{large}}", "b": "{{large}}", "c": "{{large}}" }""";

        var result = Build(Record, records: [sample]);

        Assert.Equal(AtBoundDerivation.Contract, result.Derivation);
        Assert.True(result.RecordBytes <= AtBoundSample.RecordMaximumBytes);
        var record = JsonNode.Parse(result.Document!)!.AsObject()["items"]!.AsArray()[0]!.AsObject();
        Assert.Equal(Bound, Encoding.UTF8.GetByteCount(record["description"]!.GetValue<string>()));
    }

    [Fact]
    public void TheDerivationsLimitationNamesNoValue()
    {
        var raised = Assert.Throws<AtBoundDerivationException>(() =>
            Build("""{ "type": "object", "properties": { "code": { "type": "string", "pattern": "^[0-9]{4}$" } } }"""));

        Assert.Equal("undecided", raised.Code);
        Assert.DoesNotContain("Consulting", raised.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 16), raised.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeaderIsTheVersionsOwnAndOnlyOneRecordIsKept()
    {
        const string Record = """
            { "type": "object", "properties": { "description": { "type": "string" } } }
            """;

        var result = Build(
            Record,
            records:
            [
                """{ "description": "one" }""",
                """{ "description": "two" }""",
            ]);

        var document = JsonNode.Parse(result.Document!)!.AsObject();
        Assert.Single(document["items"]!.AsArray());
        Assert.Equal("Monthly statement", document["header"]!["title"]!.GetValue<string>());
    }

    private static bool ContainsBound(JsonNode node) =>
        node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
            && Encoding.UTF8.GetByteCount(value.GetValue<string>()) == Bound
        || node is JsonObject obj && obj.Any(member => ContainsBound(member.Value!))
        || node is JsonArray array && array.Any(item => item is not null && ContainsBound(item));

    private static AtBoundSampleResult Build(
        string recordSchema,
        string[]? records = null,
        string itemAnchor = "$.description")
    {
        using var package = Packages.WritePackage(AtBoundFixtures.Package(recordSchema, records, itemAnchor));
        var candidate = Packages.ReadCandidate(package.Directory);
        var schemaBytes = File.ReadAllBytes(Path.Combine(package.Directory, "schema.json"));
        var compilation = DataContractCompiler.Compile(schemaBytes, candidate.SourceDescriptorSha256, "schema.json");
        Assert.True(compilation.Profile.IsValid, "the fixture's schema is not inside the profile: " + compilation.Profile.Rule);
        Assert.NotNull(compilation.Contract);
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(package.Directory, "schema.json")))!;
            var sample = File.ReadAllBytes(Path.Combine(package.Directory, "sample.json"));
            return AtBoundSample.Build(new AtBoundSampleInput(
                candidate,
                compilation.Contract!,
                root,
                sample,
                Bound,
                [itemAnchor]));
        }
        finally
        {
            compilation.Contract!.Dispose();
        }
    }
}
