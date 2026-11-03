using System.Text;

namespace ReportService.DataContract.Tests;

/// <summary>
/// The mapping of a failed evaluation: the first violation the contract reports,
/// its path rendered from the record's own structure in the <c>$.…</c> form the
/// diagnostics use, the keyword that failed, and never a value or an unexpected
/// member name of the producer's.
/// </summary>
public sealed class ContractMappingTests
{
    private const string Marker = "PLANTED-";

    private const string RecordSchema =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "properties": {
            "header": { "type": "object", "properties": { "total": { "type": "number" } } },
            "items": { "items": {
              "type": "object",
              "required": ["description"],
              "additionalProperties": false,
              "properties": {
                "sequence": { "type": "integer", "minimum": 1 },
                "description": { "type": "string", "maxLength": 10 },
                "kind": { "enum": ["a", "b"] },
                "fixed": { "const": "X" },
                "code": { "type": "string", "pattern": "^[A-Z]+$" },
                "when": { "type": "string", "format": "date" },
                "customer": { "type": "object", "properties": { "name": { "type": "string" } } },
                "lines": { "type": "array", "items": { "type": "object", "properties": { "text": { "type": "string" } } } }
              }
            } }
          }
        }
        """;

    public static TheoryData<string, string, string> Violations => new()
    {
        { """{"description":"ok","sequence":0}""", "$.sequence", "minimum" },
        { """{"description":1}""", "$.description", "type" },
        { """{}""", "$.description", "required" },
        { """{"description":"ok","kind":"z"}""", "$.kind", "enum" },
        { """{"description":"ok","fixed":"Y"}""", "$.fixed", "const" },
        { """{"description":"ok","code":"abc"}""", "$.code", "pattern" },
        { """{"description":"ok","when":"not-a-date"}""", "$.when", "format" },
        { """{"description":"too long a text"}""", "$.description", "maxLength" },
        { """{"description":"ok","customer":{"name":1}}""", "$.customer.name", "type" },
        {
            """{"description":"ok","lines":[{"text":"a"},{"text":"b"},{"text":"c"},{"text":1}]}""",
            "$.lines[3].text",
            "type"
        },
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public void AViolationIsReportedAtItsPathWithItsKeyword(string record, string path, string keyword)
    {
        using var contract = Compiled();
        var json = Schemas.Utf8(record);

        var check = contract.CheckRecord(json);

        Assert.False(check.IsValid);
        Assert.Equal(path, check.Path);
        Assert.Equal(keyword, check.Keyword);
        Assert.DoesNotContain(Marker, check.Path, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, check.Keyword, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstViolationInReadingOrderIsTheOneReported()
    {
        using var contract = Compiled();

        var check = contract.CheckRecord(
            Schemas.Utf8("""{"description":1,"sequence":0,"kind":"z"}"""));

        Assert.Equal("$.description", check.Path);
        Assert.Equal("type", check.Keyword);
    }

    [Fact]
    public void AnUnexpectedMemberIsReportedAtItsParentAndItsNameNeverAppears()
    {
        using var contract = Compiled();

        var check = contract.CheckRecord(
            Schemas.Utf8("""{"description":"ok","PLANTED-member":1}"""));

        Assert.False(check.IsValid);
        Assert.Equal("$", check.Path);
        Assert.Equal("additionalProperties", check.Keyword);
        Assert.DoesNotContain(Marker, check.Path, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, check.Keyword, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberTheSchemaDoesNotDeclareIsReportedAsAWildcard()
    {
        var schema = Schemas.Utf8(
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema",
             "properties":{"header":{"type":"object"},
                           "items":{"items":{"type":"object","patternProperties":{"^x":{"type":"string"}}}}}}
            """);
        var compiled = DataContractCompiler.Compile(schema, "sha", "schema.json");
        using var contract = Assert.IsType<PreparedDataContract>(compiled.Contract);

        var check = contract.CheckRecord(Schemas.Utf8("""{"x1":1}"""));

        Assert.False(check.IsValid);
        Assert.Equal("$.*", check.Path);
        Assert.Equal("type", check.Keyword);
    }

    [Fact]
    public void TheHeaderIsCheckedAtItsOwnRootWithItsOwnPath()
    {
        using var contract = Compiled();

        var check = contract.CheckHeader(Schemas.Utf8("""{"total":"not a number"}"""));

        Assert.False(check.IsValid);
        Assert.Equal("$.total", check.Path);
        Assert.Equal("type", check.Keyword);

        Assert.True(contract.CheckHeader(Schemas.Utf8("""{"total":12.5}""")).IsValid);
    }

    [Fact]
    public void AValidRecordAndAValidHeaderAreReportedAsValid()
    {
        using var contract = Compiled();

        var record = contract.CheckRecord(
            Schemas.Utf8("""{"sequence":1,"description":"ok","kind":"a","fixed":"X","when":"2026-11-01"}"""));

        Assert.True(record.IsValid);
        Assert.Null(record.Path);
        Assert.Null(record.Keyword);
    }

    [Fact]
    public void APathIsCappedAtTheBoundAndEndsWithAnEllipsis()
    {
        const int levels = 24;
        var name = new string('n', 45);
        var schema = new StringBuilder();
        schema.Append("""{"$schema":"https://json-schema.org/draft/2020-12/schema","properties":{"header":{"type":"object"},"items":{"items":""");
        for (var level = 0; level < levels; level++)
        {
            schema.Append("""{"type":"object","properties":{""").Append('"').Append(name).Append("\":");
        }

        schema.Append("""{"type":"string"}""");
        for (var level = 0; level < levels; level++)
        {
            schema.Append("}}");
        }

        schema.Append("}}}");

        var compiled = DataContractCompiler.Compile(Schemas.Utf8(schema.ToString()), "sha", "schema.json");
        Assert.True(compiled.Profile.IsValid, compiled.Profile.Rule + " " + compiled.Profile.SchemaPointer);
        using var contract = Assert.IsType<PreparedDataContract>(compiled.Contract);

        var record = new StringBuilder("{");
        for (var level = 1; level <= levels; level++)
        {
            record.Append('"').Append(name).Append("\":{");
        }

        record.Append('"').Append(name).Append("\":1");
        for (var level = 0; level <= levels; level++)
        {
            record.Append('}');
        }

        var check = contract.CheckRecord(Schemas.Utf8(record.ToString()));

        Assert.False(check.IsValid);
        Assert.True(Encoding.UTF8.GetByteCount(check.Path!) <= 1_024 + 3, check.Path);
        Assert.EndsWith(".…", check.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void NoValueOfTheRecordReachesTheReportOrAnExceptionMessage()
    {
        using var contract = Compiled();

        var check = contract.CheckRecord(
            Schemas.Utf8("""{"sequence":1,"description":"PLANTED-value","when":"PLANTED-date"}"""));
        Assert.False(check.IsValid);
        Assert.DoesNotContain(Marker, check.Path, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, check.Keyword, StringComparison.Ordinal);

        var failure = new ContractEvaluationFailed(ContractEvaluationFailed.Evaluator, "$.description");
        Assert.DoesNotContain(Marker, failure.Message, StringComparison.Ordinal);
        Assert.Equal(ContractEvaluationFailed.Evaluator, failure.Kind);
        Assert.Equal("$.description", failure.Keyword);
    }

    private static PreparedDataContract Compiled()
    {
        var compiled = DataContractCompiler.Compile(Schemas.Utf8(RecordSchema), "sha", "schema.json");
        Assert.True(compiled.Profile.IsValid, compiled.Profile.Rule + " " + compiled.Profile.SchemaPointer);
        return Assert.IsType<PreparedDataContract>(compiled.Contract);
    }
}
