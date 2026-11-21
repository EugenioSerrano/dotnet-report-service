namespace ReportService.DataContract.Tests;

/// <summary>
/// The profile: every rule of the restricted template profile refuses its own
/// case, the smallest admissible twin of the same rule passes, and a refusal
/// names the rule and a place inside the schema — never anything of the data.
/// </summary>
public sealed class DataContractProfileTests
{
    [Fact]
    public void AnEnvelopeInsideTheProfileIsAdmitted()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(Schemas.ValidEnvelope));

        Assert.True(result.IsValid);
        Assert.Null(result.Rule);
        Assert.Null(result.SchemaPointer);
    }

    [Fact]
    public void ASchemaOneByteOverTheSizeCeilingIsRefused()
    {
        var admitted = DataContractProfile.Check(Schemas.OfSize(65_536));
        var refused = DataContractProfile.Check(Schemas.OfSize(65_537));

        Assert.True(admitted.IsValid, admitted.Rule);
        AssertRefused(refused, "size", "");
    }

    [Fact]
    public void ASchemaThatDoesNotDeclareTheDialectIsRefused()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """{"properties":{"header":{"type":"object"},"items":{"items":{"type":"object"}}}}"""));

        AssertRefused(result, "dialect", "/$schema");
    }

    [Fact]
    public void ASchemaOfAnotherDialectIsRefused()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2019-09/schema",
             "properties":{"header":{"type":"object"},"items":{"items":{"type":"object"}}}}
            """));

        AssertRefused(result, "dialect", "/$schema");
    }

    [Fact]
    public void ADocumentThatIsNotAnObjectIsRefused()
    {
        var result = DataContractProfile.Check(Schemas.Utf8("[1,2]"));

        AssertRefused(result, "dialect", "");
    }

    [Theory]
    [InlineData("$id", "\"$id\":\"https://example.invalid/x\",")]
    [InlineData("$anchor", "\"$anchor\":\"a\",")]
    [InlineData("$dynamicRef", "\"$dynamicRef\":\"#a\",")]
    [InlineData("$dynamicAnchor", "\"$dynamicAnchor\":\"a\",")]
    [InlineData("$recursiveRef", "\"$recursiveRef\":\"#\",")]
    [InlineData("$vocabulary", "\"$vocabulary\":{},")]
    [InlineData("contentEncoding", "\"contentEncoding\":\"base64\",")]
    [InlineData("unevaluatedItems", "\"unevaluatedItems\":false,")]
    public void AKeywordOutsideTheProfileIsRefused(string keyword, string member)
    {
        var root =
            $"{{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"properties\":{{\"header\":{{\"type\":\"object\",{member}\"type\":\"object\"}},\"items\":{{\"items\":{{\"type\":\"object\"}}}}}}}}";

        var result = DataContractProfile.Check(Schemas.Utf8(root));

        AssertRefused(result, "keyword", $"/properties/header/{keyword}");
    }

    [Fact]
    public void ADialectMemberBelowTheRootIsRefused()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"$schema":"https://json-schema.org/draft/2020-12/schema"},"items":{"items":{"type":"object"}}}}
            """));

        AssertRefused(result, "keyword", "/properties/header/$schema");
    }

    [Fact]
    public void ASchemaPositionThatIsNotAnObjectOrABooleanIsRefused()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":"anything","items":{"items":{"type":"object"}}}}
            """));

        AssertRefused(result, "schema", "/properties/header");
    }

    [Fact]
    public void SchemaPositionsNestedToTheDepthCeilingAreAdmitted()
    {
        var result = DataContractProfile.Check(Schemas.WithHeaderDepth(64));

        Assert.True(result.IsValid, result.Rule + " " + result.SchemaPointer);
    }

    [Fact]
    public void SchemaPositionsNestedOneLevelPastTheDepthCeilingAreRefused()
    {
        var result = DataContractProfile.Check(Schemas.WithHeaderDepth(65));

        Assert.Equal("depth", result.Rule);
        Assert.Contains("/properties/", result.SchemaPointer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1_024, true)]
    [InlineData(1_025, false)]
    public void APatternIsAdmittedToItsLengthCeilingAndRefusedPastIt(int characters, bool admitted)
    {
        var pattern = new string('a', characters);
        var schema =
            "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"properties\":{\"header\":"
            + "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\",\"pattern\":\"" + pattern + "\"}}},"
            + "\"items\":{\"items\":{\"type\":\"object\"}}}}";

        var result = DataContractProfile.Check(Schemas.Utf8(schema));

        if (admitted)
        {
            Assert.True(result.IsValid, result.Rule + " " + result.SchemaPointer);
        }
        else
        {
            AssertRefused(result, "pattern", "/properties/header/properties/value/pattern");
        }
    }

    [Fact]
    public void APatternThatDoesNotCompileIsRefused()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"type":"object","properties":{"value":{"type":"string","pattern":"[a-"}}},
                           "items":{"items":{"type":"object"}}}}
            """));

        AssertRefused(result, "pattern", "/properties/header/properties/value/pattern");
    }

    [Fact]
    public void APatternPropertyKeyIsCheckedAsAPattern()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"type":"object","patternProperties":{"[a-":{"type":"string"}}},
                           "items":{"items":{"type":"object"}}}}
            """));

        AssertRefused(result, "pattern", "/properties/header/patternProperties/[a-");
    }

    [Fact]
    public void AnUnknownFormatIsRefused()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"type":"object","properties":{"value":{"type":"string","format":"hostname"}}},
                           "items":{"items":{"type":"object"}}}}
            """));

        AssertRefused(result, "format", "/properties/header/properties/value/format");
    }

    [Fact]
    public void AFormatThatIsNotANameIsRefused()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"type":"object","properties":{"value":{"type":"string","format":7}}},
                           "items":{"items":{"type":"object"}}}}
            """));

        AssertRefused(result, "format", "/properties/header/properties/value/format");
    }

    [Theory]
    [InlineData("\"other.json#/definitions/x\"")]
    [InlineData("\"https://example.invalid/schema.json#/$defs/x\"")]
    [InlineData("\"#/$defs/absent\"")]
    [InlineData("\"#\"")]
    [InlineData("7")]
    public void AReferenceOutsideTheDocumentIsRefused(string reference)
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"$ref":
            """
            + reference
            + """
            },"items":{"items":{"type":"object"}}}}
            """));

        AssertRefused(result, "reference", "/properties/header/$ref");
    }

    [Fact]
    public void AReferenceToAFragmentOfTheDocumentIsAdmitted()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"$ref":"#/$defs/h"},"items":{"items":{"type":"object"}}},
             "$defs":{"h":{"type":"object"}}}
            """));

        Assert.True(result.IsValid, result.Rule + " " + result.SchemaPointer);
    }

    [Fact]
    public void ACycleThatNeverDescendsIntoTheInstanceIsRefused()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"$ref":"#/$defs/a"},"items":{"items":{"type":"object"}}},
             "$defs":{"a":{"allOf":[{"$ref":"#/$defs/b"}]},"b":{"anyOf":[{"$ref":"#/$defs/a"}]}}}
            """));

        Assert.Equal("cycle", result.Rule);
        Assert.False(string.IsNullOrEmpty(result.SchemaPointer));
    }

    [Fact]
    public void ARecursiveTreeThatDescendsIntoTheInstanceIsAdmitted()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"$ref":"#/$defs/node"},"items":{"items":{"type":"object"}}},
             "$defs":{"node":{"type":"object","properties":{"child":{"$ref":"#/$defs/node"}}}}}
            """));

        Assert.True(result.IsValid, result.Rule + " " + result.SchemaPointer);
    }

    [Theory]
    [InlineData("""{"$schema":"https://json-schema.org/draft/2020-12/schema","properties":{"items":{"items":{"type":"object"}}}}""", "/properties/header")]
    [InlineData("""{"$schema":"https://json-schema.org/draft/2020-12/schema","properties":{"header":{"type":"object"}}}""", "/properties/items/items")]
    [InlineData("""{"$schema":"https://json-schema.org/draft/2020-12/schema","properties":{"header":{"type":"object"},"items":{"type":"array"}}}""", "/properties/items/items")]
    public void ASchemaWithoutBothRootsIsRefused(string schema, string where)
    {
        var result = DataContractProfile.Check(Schemas.Utf8(schema));

        AssertRefused(result, "envelope", where);
    }

    [Fact]
    public void AHeaderRootBehindAReferenceIsAdmitted()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"$ref":"#/$defs/h"},"items":{"items":{"$ref":"#/$defs/r"}}},
             "$defs":{"h":{"type":"object"},"r":{"type":"object"}}}
            """));

        Assert.True(result.IsValid, result.Rule + " " + result.SchemaPointer);
    }

    [Fact]
    public void ABooleanSchemaAtARootIsAdmitted()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":true,"items":{"items":false}}}
            """));

        Assert.True(result.IsValid, result.Rule + " " + result.SchemaPointer);
    }

    [Fact]
    public void ValuesThatLookLikeKeywordsInsideEnumAndExamplesAreData()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"type":"object","properties":{"kind":{"enum":["$id","pattern","format"]}}},
                           "items":{"items":{"type":"object"}}},
             "examples":[{"$ref":"not a reference","$schema":"not a dialect"}]}
            """));

        Assert.True(result.IsValid, result.Rule + " " + result.SchemaPointer);
    }

    [Fact]
    public void ASchemaThatIsNotJsonIsRefusedWithoutThrowing()
    {
        var result = DataContractProfile.Check(Schemas.Utf8("""{"$schema":}"""));

        Assert.Equal("compile", result.Rule);
    }

    [Fact]
    public void AnEmptyDocumentIsRefusedWithoutThrowing()
    {
        var result = DataContractProfile.Check(ReadOnlyMemory<byte>.Empty);

        Assert.False(result.IsValid);
    }

    private static void AssertRefused(ProfileResult result, string rule, string pointer)
    {
        Assert.False(result.IsValid);
        Assert.Equal(rule, result.Rule);
        Assert.Equal(pointer, result.SchemaPointer);
    }
}
