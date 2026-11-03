namespace ReportService.DataContract.Tests;

/// <summary>
/// The edges of the contract and of its cache: the shapes the profile refuses as a
/// shape rather than as a rule, the fallback a contract reports when no assertion
/// keyword names the failure, a contract after it is disposed, and the cache's own
/// disposal and leasing rules.
/// </summary>
public sealed class ContractEdgeTests
{
    [Fact]
    public void ADialectMemberThatIsNotANameIsRefusedAsADialect()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """{"$schema":7,"properties":{"header":{"type":"object"},"items":{"items":{"type":"object"}}}}"""));

        Assert.Equal("dialect", result.Rule);
        Assert.Equal("/$schema", result.SchemaPointer);
    }

    [Fact]
    public void ADefinitionsMemberThatIsNotAnObjectIsRefusedAsAShape()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema","$defs":1,
             "properties":{"header":{"type":"object"},"items":{"items":{"type":"object"}}}}
            """));

        Assert.Equal("schema", result.Rule);
        Assert.Equal("/$defs", result.SchemaPointer);
    }

    [Fact]
    public void APropertiesMemberThatIsNotAnObjectIsRefusedAsAShape()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """{"$schema":"https://json-schema.org/draft/2020-12/schema","properties":1}"""));

        Assert.Equal("schema", result.Rule);
        Assert.Equal("/properties", result.SchemaPointer);
    }

    [Fact]
    public void APrefixItemsMemberThatIsNotAnArrayIsRefusedAsAShape()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema","prefixItems":1,
             "properties":{"header":{"type":"object"},"items":{"items":{"type":"object"}}}}
            """));

        Assert.Equal("schema", result.Rule);
        Assert.Equal("/prefixItems", result.SchemaPointer);
    }

    [Fact]
    public void ABooleanSchemaOfAPrefixItemsListAndADependentSchemaAreAdmitted()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"type":"object","dependentSchemas":{"a":{"required":["b"]}}},
                           "items":{"items":{"prefixItems":[true,{"type":"object"}]}}}}
            """));

        Assert.True(result.IsValid, result.Rule + " " + result.SchemaPointer);
    }

    [Fact]
    public void AChainOfCombinatorsThatNeverReturnsToItselfIsAdmitted()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"allOf":[{"anyOf":[{"type":"object"},{"not":{"type":"string"}}]}]},
                           "items":{"items":{"type":"object"}}}}
            """));

        Assert.True(result.IsValid, result.Rule + " " + result.SchemaPointer);
    }

    [Fact]
    public void ARecordSchemaThatAdmitsNothingReportsTheSchemaKeywordAtTheRoot()
    {
        using var contract = Compile(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"type":"object"},"items":{"items":false}}}
            """);

        var check = contract.CheckRecord(Schemas.Utf8("""{"a":1}"""));

        Assert.False(check.IsValid);
        Assert.Equal("$", check.Path);
        Assert.Equal("schema", check.Keyword);
    }

    [Fact]
    public void AnUnexpectedMemberOfAnArrayElementIsReportedAtThatElement()
    {
        using var contract = Compile(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"type":"object"},
                           "items":{"items":{"type":"object","properties":{
                             "lines":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"text":{"type":"string"}}}}}}}}}
            """);

        var check = contract.CheckRecord(
            Schemas.Utf8("""{"lines":[{"text":"a"},{"text":"b","PLANTED-extra":1}]}"""));

        Assert.False(check.IsValid);
        Assert.Equal("$.lines[1]", check.Path);
        Assert.Equal("additionalProperties", check.Keyword);
        Assert.DoesNotContain("PLANTED", check.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void AContractThatHasBeenDisposedRefusesToEvaluate()
    {
        var compiled = DataContractCompiler.Compile(Schemas.Utf8(Schemas.ValidEnvelope), "sha", "schema.json");
        var contract = Assert.IsType<PreparedDataContract>(compiled.Contract);
        contract.Dispose();
        contract.Dispose();

        Assert.True(contract.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => contract.CheckRecord(Schemas.Utf8("{}")));
    }

    [Fact]
    public void ALibraryVersionIsNamedForTheCacheKey()
    {
        Assert.Equal("corvus-text-json-5.6.1", DataContractCompiler.LibraryVersion);
    }

    [Fact]
    public void DisposingTheCacheDisposesTheContractsNobodyIsUsingAndSparesTheLeasedOne()
    {
        var cache = new PreparedDataContractCache();
        var lease = cache.Lease("sha-lease", "schema.json", Schemas.Utf8(Schemas.ValidEnvelope));
        var leasedContract = lease.Compilation.Contract!;

        PreparedDataContract idle;
        using (var other = cache.Lease("sha-idle", "schema.json", Schemas.Utf8(Schemas.ValidEnvelope)))
        {
            idle = other.Compilation.Contract!;
        }

        cache.Dispose();

        Assert.True(idle.IsDisposed);
        Assert.False(leasedContract.IsDisposed);

        lease.Dispose();

        Assert.True(leasedContract.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => cache.Lease("sha-lease", "schema.json", Schemas.Utf8(Schemas.ValidEnvelope)));
    }

    [Fact]
    public void ALeaseDisposedTwiceReleasesItsEntryOnce()
    {
        var cache = new PreparedDataContractCache();
        var lease = cache.Lease("sha-a", "schema.json", Schemas.Utf8(Schemas.ValidEnvelope));
        var contract = lease.Compilation.Contract!;

        lease.Dispose();
        lease.Dispose();

        using var again = cache.Lease("sha-a", "schema.json", Schemas.Utf8(Schemas.ValidEnvelope));

        Assert.Same(contract, again.Compilation.Contract);
        Assert.False(contract.IsDisposed);
        Assert.Equal(1, cache.CompilationCount);
    }

    [Fact]
    public void ConcurrentFirstUseOfOneSchemaCompilesItOnceAndAnswersAlike()
    {
        var cache = new PreparedDataContractCache();
        var schema = Schemas.Utf8(Schemas.ValidEnvelope);
        var ready = new Barrier(4);
        var verdicts = new bool[4];

        Parallel.For(0, 4, worker =>
        {
            ready.SignalAndWait();
            using var lease = cache.Lease("sha-a", "schema.json", schema);
            verdicts[worker] = lease.Compilation.Contract!.CheckRecord(Schemas.Utf8("""{"a":1}""")).IsValid;
        });

        Assert.All(verdicts, Assert.True);
        Assert.Equal(1, cache.CompilationCount);
    }

    [Fact]
    public void EveryAdmittedKeywordIsInsideTheProfile()
    {
        var result = DataContractProfile.Check(Schemas.Utf8(
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$comment": "the admitted set",
              "title": "t", "description": "d", "default": 1, "examples": [1, 2],
              "deprecated": false, "readOnly": false, "writeOnly": false,
              "type": ["object", "null"],
              "properties": {
                "header": {
                  "type": "object",
                  "enum": [{"a": 1}],
                  "const": {"a": 1},
                  "required": ["a"],
                  "additionalProperties": {"type": "string"},
                  "unevaluatedProperties": false,
                  "patternProperties": {"^a": {"type": "string"}},
                  "propertyNames": {"pattern": "^[a-z]+$"},
                  "items": {"type": "string"},
                  "prefixItems": [true, {"type": "object"}],
                  "contains": {"type": "string"},
                  "minContains": 0,
                  "maxContains": 2,
                  "minItems": 0,
                  "maxItems": 4,
                  "uniqueItems": true,
                  "minLength": 0,
                  "maxLength": 8,
                  "pattern": "^[a-z]+$",
                  "minimum": 0,
                  "maximum": 8,
                  "exclusiveMinimum": -1,
                  "exclusiveMaximum": 9,
                  "multipleOf": 2,
                  "allOf": [true],
                  "anyOf": [{"type": "object"}],
                  "oneOf": [{"type": "object"}],
                  "not": {"type": "string"},
                  "if": {"required": ["a"]},
                  "then": {"required": ["b"]},
                  "else": {"required": ["c"]},
                  "dependentRequired": {"a": ["b"]},
                  "dependentSchemas": {"a": {"required": ["b"]}},
                  "format": "date"
                },
                "items": {"items": {"$ref": "#/$defs/record"}}
              },
              "$defs": {"record": {"type": "object"}}
            }
            """));

        Assert.True(result.IsValid, result.Rule + " " + result.SchemaPointer);
    }

    [Fact]
    public void APropertyNamesViolationIsReportedAtItsParent()
    {
        using var contract = Compile(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"type":"object"},
                           "items":{"items":{"type":"object","propertyNames":{"pattern":"^[a-z]+$"}}}}}
            """);

        var check = contract.CheckRecord(Schemas.Utf8("""{"PLANTED-Name":1}"""));

        Assert.False(check.IsValid);
        Assert.Equal("$", check.Path);
        Assert.Equal("propertyNames", check.Keyword);
    }

    [Fact]
    public void AnUnevaluatedPropertyIsReportedAtItsParent()
    {
        using var contract = Compile(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"type":"object"},
                           "items":{"items":{"type":"object","properties":{"text":{"type":"string"}},
                                             "unevaluatedProperties":false}}}}
            """);

        var check = contract.CheckRecord(Schemas.Utf8("""{"text":"a","PLANTED-other":1}"""));

        Assert.False(check.IsValid);
        Assert.Equal("$", check.Path);
        Assert.Equal("unevaluatedProperties", check.Keyword);
    }

    [Fact]
    public void ADeclaredMemberWhoseNameIsNotAnIdentifierIsBracketed()
    {
        using var contract = Compile(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"type":"object"},
                           "items":{"items":{"type":"object","properties":{"na me":{"type":"string"}}}}}}
            """);

        var check = contract.CheckRecord(Schemas.Utf8("""{"na me":1}"""));

        Assert.False(check.IsValid);
        Assert.Equal("$['na me']", check.Path);
        Assert.Equal("type", check.Keyword);
    }

    [Fact]
    public void DisposingTheCacheTwiceIsHarmless()
    {
        var cache = new PreparedDataContractCache();
        using (cache.Lease("sha-a", "schema.json", Schemas.Utf8(Schemas.ValidEnvelope)))
        {
        }

        cache.Dispose();
        cache.Dispose();

        Assert.Throws<ObjectDisposedException>(() => cache.Lease("sha-a", "schema.json", Schemas.Utf8(Schemas.ValidEnvelope)));
    }

    [Fact]
    public void AnEvictedRefusalIsKeptWithoutAContract()
    {
        var cache = new PreparedDataContractCache();
        var refused = Schemas.Utf8("""{"not":"an envelope"}""");

        using (var first = cache.Lease("sha-refused", "schema.json", refused))
        {
            Assert.Null(first.Compilation.Contract);
        }

        for (var index = 0; index < PreparedDataContractCache.MaximumEntries + 1; index++)
        {
            using var other = cache.Lease("sha-fill-" + index, "schema.json", Schemas.Utf8(Schemas.ValidEnvelope));
        }

        using var again = cache.Lease("sha-refused", "schema.json", refused);

        Assert.Null(again.Compilation.Contract);
        Assert.False(again.Compilation.Profile.IsValid);
    }

    [Fact]
    public void TheLibraryDefaultsSeamRefusesADocumentOutsideTheProfile()
    {
        var compiled = DataContractCompiler.CompileWithLibraryDefaults(
            Schemas.Utf8("""{"not":"an envelope"}"""), "sha", "schema.json");

        Assert.False(compiled.Profile.IsValid);
        Assert.Null(compiled.Contract);
    }

    private static PreparedDataContract Compile(string schema)
    {
        var compiled = DataContractCompiler.Compile(Schemas.Utf8(schema), "sha", "schema.json");
        Assert.True(compiled.Profile.IsValid, compiled.Profile.Rule + " " + compiled.Profile.SchemaPointer);
        return Assert.IsType<PreparedDataContract>(compiled.Contract);
    }
}
