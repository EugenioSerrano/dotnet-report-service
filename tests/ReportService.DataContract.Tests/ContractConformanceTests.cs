using System.Text;
using System.Text.Json;

namespace ReportService.DataContract.Tests;

/// <summary>
/// Conformance: the three pilot templates' schemas are inside the profile, their
/// samples conform at both roots, each of the nine asserted formats accepts a
/// conforming value and rejects a non-conforming one, and a schema that cannot be
/// compiled is a refusal rather than an exception.
/// </summary>
public sealed class ContractConformanceTests
{
    [Theory]
    [InlineData("pilot-simple")]
    [InlineData("pilot-grouped")]
    [InlineData("pilot-volume")]
    public void APilotSchemaIsInsideTheProfileAndItsSampleConformsAtBothRoots(string template)
    {
        var compiled = DataContractCompiler.Compile(Schemas.PilotSchema(template), "sha-" + template, "schema.json");

        Assert.True(compiled.Profile.IsValid, compiled.Profile.Rule + " " + compiled.Profile.SchemaPointer);
        using var contract = Assert.IsType<PreparedDataContract>(compiled.Contract);
        using var sample = JsonDocument.Parse(Schemas.PilotSample(template));

        var header = JsonSerializer.SerializeToUtf8Bytes(sample.RootElement.GetProperty("header"));
        var headerCheck = contract.CheckHeader(header);
        Assert.True(headerCheck.IsValid, headerCheck.Path + " " + headerCheck.Keyword);

        var records = 0;
        foreach (var item in sample.RootElement.GetProperty("items").EnumerateArray())
        {
            records++;
            var check = contract.CheckRecord(JsonSerializer.SerializeToUtf8Bytes(item));
            Assert.True(check.IsValid, $"record {records}: {check.Path} {check.Keyword}");
        }

        Assert.True(records > 0);
    }

    [Theory]
    [InlineData("date", "2026-11-01", "2026-13-01")]
    [InlineData("date-time", "2026-11-01T00:00:00Z", "2026-11-01T25:00:00Z")]
    [InlineData("time", "13:45:30Z", "13:45:30")]
    [InlineData("duration", "P1DT2H", "1 day")]
    [InlineData("email", "a@example.com", "a@")]
    [InlineData("uri", "https://example.invalid/a", "not a uri")]
    [InlineData("uuid", "3f0a4c1e-8a5e-4b4f-9c2d-1f0e5a6b7c8d", "abcd")]
    [InlineData("ipv4", "192.0.2.1", "999.1.1.1")]
    [InlineData("ipv6", "2001:db8::1", "2001:db8:::1")]
    public void AnAssertedFormatAcceptsAConformingValueAndRejectsANonConformingOne(
        string format, string conforming, string nonConforming)
    {
        using var contract = Compiled(Schemas.WithHeaderString(format));

        Assert.True(contract.CheckHeader(Header(conforming)).IsValid);

        var check = contract.CheckHeader(Header(nonConforming));

        Assert.False(check.IsValid);
        Assert.Equal("format", check.Keyword);
        Assert.Equal("$.value", check.Path);
    }

    [Fact]
    public void WithTheLibraryDefaultsAnInvalidDateWouldBeAccepted()
    {
        // The control behind the fixed options: format is an annotation unless the
        // options say otherwise, which is why the contract asserts it.
        var schema = Schemas.WithHeaderString("date");
        var defaults = DataContractCompiler.CompileWithLibraryDefaults(schema, "sha", "schema.json");

        Assert.True(defaults.Contract!.CheckHeader(Header("2026-13-01")).IsValid);

        using var contract = Compiled(schema);
        Assert.False(contract.CheckHeader(Header("2026-13-01")).IsValid);
    }

    [Fact]
    public void ASchemaThatCannotBeCompiledIsARefusalAndNotAnException()
    {
        var compiled = DataContractCompiler.Compile(
            Schemas.Utf8(
                """
                {"$schema":"https://json-schema.org/draft/2020-12/schema",
                 "properties":{"header":{"$ref":"#/$defs/absent"},"items":{"items":{"type":"object"}}}}
                """),
            "sha",
            "schema.json");

        Assert.False(compiled.Profile.IsValid);
        Assert.Null(compiled.Contract);
    }

    [Fact]
    public void AContractWhoseSchemaCannotBeEvaluatedIsCompiledWithoutChecksAndFailsAtEvaluation()
    {
        var recursive = Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"$ref":"#"},"items":{"items":{"type":"object"}}}}
            """);

        var refused = DataContractCompiler.Compile(recursive, "sha", "schema.json");
        Assert.Equal("reference", refused.Profile.Rule);

        var bypassing = DataContractCompiler.CompileWithoutProfileChecks(recursive, "sha", "schema.json");
        var nested = string.Concat(Enumerable.Repeat("""{"a":""", 70)) + "1" + new string('}', 70);

        var failure = Assert.Throws<ContractEvaluationFailed>(
            () => bypassing.Contract!.CheckHeader(Schemas.Utf8(nested)));
        Assert.Equal(ContractEvaluationFailed.Depth, failure.Kind);
    }

    [Fact]
    public void ACatastrophicPatternOverItsTimeLimitFailsAsPatternTimeAndNeverHangs()
    {
        var schema = Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"type":"object","properties":{"value":{"type":"string","pattern":"^(a+)+$"}}},
                           "items":{"items":{"type":"object"}}}}
            """);

        using var contract = Assert.IsType<PreparedDataContract>(
            DataContractCompiler.CompileWithOptions(schema, "sha", "schema.json", TimeSpan.FromMilliseconds(100)).Contract);

        var failure = Assert.Throws<ContractEvaluationFailed>(
            () => contract.CheckHeader(Header(new string('a', 40) + "!")));

        Assert.Equal(ContractEvaluationFailed.PatternTime, failure.Kind);
        Assert.DoesNotContain(new string('a', 40), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceOutsideTheDocumentIsNeverResolved()
    {
        var schema = Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"$ref":"https://example.invalid/schema.json#/$defs/h"},"items":{"items":{"type":"object"}}}}
            """);

        var compiled = DataContractCompiler.Compile(schema, "sha", "schema.json");

        Assert.Equal("reference", compiled.Profile.Rule);

        var unchecked_ = DataContractCompiler.CompileWithoutProfileChecks(schema, "sha", "schema.json");
        Assert.Equal("compile", unchecked_.Profile.Rule);
        Assert.Null(unchecked_.Contract);
    }

    private static PreparedDataContract Compiled(byte[] schema)
    {
        var compiled = DataContractCompiler.Compile(schema, "sha", "schema.json");
        Assert.True(compiled.Profile.IsValid, compiled.Profile.Rule + " " + compiled.Profile.SchemaPointer);
        return Assert.IsType<PreparedDataContract>(compiled.Contract);
    }

    private static byte[] Header(string value) => Encoding.UTF8.GetBytes("{\"value\":\"" + value + "\"}");
}
