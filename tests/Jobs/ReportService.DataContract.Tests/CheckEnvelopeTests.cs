using System.Text;

namespace ReportService.DataContract.Tests;

/// <summary>
/// The envelope root: the whole sample evaluated against the schema's root, the
/// same prepared contract the header and record roots come from. A violation names
/// the path relative to the envelope and the keyword, never a value.
/// </summary>
public sealed class CheckEnvelopeTests
{
    private static readonly byte[] Schema = Encoding.UTF8.GetBytes(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "header": { "type": "object", "properties": { "title": { "type": "string" } }, "required": ["title"] },
            "items": { "type": "array", "items": { "type": "object", "properties": { "quantity": { "type": "integer" } }, "required": ["quantity"] } }
          },
          "required": ["header", "items"]
        }
        """);

    [Fact]
    public void AConformingEnvelopeIsValid()
    {
        using var contract = Compile();
        var check = contract.CheckEnvelope(Encoding.UTF8.GetBytes("""{ "header": { "title": "Invoice" }, "items": [ { "quantity": 2 } ] }"""));

        Assert.True(check.IsValid);
    }

    [Fact]
    public void AViolationNamesItsPathAndKeywordRelativeToTheEnvelope()
    {
        using var contract = Compile();
        var check = contract.CheckEnvelope(Encoding.UTF8.GetBytes("""{ "header": { "title": "Invoice" }, "items": [ { "quantity": "two" } ] }"""));

        Assert.False(check.IsValid);
        Assert.Equal("$.items[0].quantity", check.Path);
        Assert.Equal("type", check.Keyword);
    }

    [Fact]
    public void ADocumentOfAnotherShapeIsRefusedAtTheRoot()
    {
        using var contract = Compile();
        var check = contract.CheckEnvelope(Encoding.UTF8.GetBytes("[]"));

        Assert.False(check.IsValid);
        Assert.Equal("$", check.Path);
    }

    private static PreparedDataContract Compile()
    {
        var compilation = DataContractCompiler.Compile(Schema, new string('a', 64), "schema.json");
        Assert.True(compilation.Profile.IsValid);
        return Assert.IsType<PreparedDataContract>(compilation.Contract);
    }
}
