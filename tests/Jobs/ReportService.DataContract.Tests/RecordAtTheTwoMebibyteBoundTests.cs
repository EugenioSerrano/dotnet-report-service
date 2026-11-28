using System.Text;

namespace ReportService.DataContract.Tests;

/// <summary>
/// Record-local validation at the record bound: a record of two mebibytes, nearly all of it one
/// base64 image string, is validated against a contract whose item declares a <c>pattern</c> on
/// that field - valid when the value matches, and refused at the field with the keyword when it
/// does not.
/// </summary>
public sealed class RecordAtTheTwoMebibyteBoundTests
{
    private const int RecordBytes = 2_097_152;

    private const string Schema = """
        {"$schema":"https://json-schema.org/draft/2020-12/schema",
         "properties":{"header":{"type":"object"},
                       "items":{"items":{"type":"object","required":["sequence","picture"],
                         "properties":{"sequence":{"type":"integer","minimum":1},
                                       "picture":{"type":"string","pattern":"^data:image/png;base64,[A-Za-z0-9+/]*={0,2}$"}}}}}}
        """;

    [Fact]
    public void ATwoMebibyteRecordWhoseImageMatchesThePatternIsValid()
    {
        using var contract = Compile();

        var check = contract.CheckRecord(Record('A'));

        Assert.True(check.IsValid, check.Path + " " + check.Keyword);
    }

    [Fact]
    public void ATwoMebibyteRecordWhoseImageBreaksThePatternIsRefusedAtTheField()
    {
        using var contract = Compile();

        var check = contract.CheckRecord(Record('-'));

        Assert.False(check.IsValid);
        Assert.Equal("$.picture", check.Path);
        Assert.Equal("pattern", check.Keyword);
    }

    // A record of exactly two mebibytes of wire JSON whose picture body ends with the given
    // character, so the pattern is decided by the very last byte of the value.
    private static byte[] Record(char last)
    {
        const string prefix = "{\"sequence\":1,\"picture\":\"data:image/png;base64,";
        const string suffix = "\"}";
        var body = new string('A', RecordBytes - prefix.Length - suffix.Length - 1) + last;
        var bytes = Encoding.UTF8.GetBytes(prefix + body + suffix);
        Assert.Equal(RecordBytes, bytes.Length);
        return bytes;
    }

    private static PreparedDataContract Compile()
    {
        var compiled = DataContractCompiler.Compile(Schemas.Utf8(Schema), "sha", "schema.json");
        Assert.True(compiled.Profile.IsValid, compiled.Profile.Rule + " " + compiled.Profile.SchemaPointer);
        return Assert.IsType<PreparedDataContract>(compiled.Contract);
    }
}
