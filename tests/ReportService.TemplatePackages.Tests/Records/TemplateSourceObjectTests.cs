using System.Text.Json;
using ReportService.TemplatePackages.Records;

namespace ReportService.TemplatePackages.Tests.Provenance;

/// <summary>
/// The one document a taken version keeps of the files the take verified: the
/// writer orders the files by their path, the reader round-trips every value,
/// and both refuse nothing the other could have produced — a member added or
/// removed, a wrong type, a path outside the package grammar, a repeated path, a
/// negative size, a hash that is not lower-case hexadecimal and a document that
/// is not a list are each refused, and the refusal names the member rather than
/// repeating a value larger than the defect needs.
/// </summary>
public sealed class TemplateSourceObjectTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void TheEmptyListIsAnEmptyDocumentAndReadsBackEmpty()
    {
        Assert.Equal("[]", TemplateSourceObjects.Write([]));
        Assert.Empty(TemplateSourceObjects.Read("[]"));
    }

    [Fact]
    public void TheWriterOrdersTheFilesByTheirPath()
    {
        var written = TemplateSourceObjects.Write(
        [
            new TemplateSourceObject("template.yaml", 868, new string('b', 64)),
            new TemplateSourceObject("html/row.html.sbn", 24, new string('c', 64)),
            new TemplateSourceObject("schema.json", 1204, Sha),
        ]);

        using var document = JsonDocument.Parse(written);
        Assert.Equal(
            ["html/row.html.sbn", "schema.json", "template.yaml"],
            document.RootElement.EnumerateArray().Select(element => element.GetProperty("path").GetString()));
    }

    [Fact]
    public void TheRoundTripPreservesEveryValue()
    {
        TemplateSourceObject[] objects =
        [
            new("schema.json", 1204, Sha),
            new("template.yaml", 868, new string('b', 64)),
        ];

        var read = TemplateSourceObjects.Read(TemplateSourceObjects.Write(objects));

        Assert.Equal(objects, read);
        Assert.Equal(1204, read[0].Size);
        Assert.Equal(Sha, read[0].Sha256);
        Assert.Equal(868, read[1].Size);
    }

    [Fact]
    public void AMemberTheWriterNeverWritesIsRefused()
    {
        var error = Assert.Throws<JsonException>(() => TemplateSourceObjects.Read(
            $$"""[{"path":"schema.json","size":1,"sha256":"{{Sha}}","mode":"0644"}]"""));

        Assert.Contains("path", error.Message, StringComparison.Ordinal);
        Assert.Contains("sha256", error.Message, StringComparison.Ordinal);
        Assert.Contains("size", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberTheWriterAlwaysWritesMustBePresent()
    {
        var error = Assert.Throws<JsonException>(() => TemplateSourceObjects.Read(
            """[{"path":"schema.json","size":1}]"""));

        Assert.Contains("sha256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APathThatIsNotAStringIsRefused()
    {
        var error = Assert.Throws<JsonException>(() => TemplateSourceObjects.Read(
            """[{"path":7,"size":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]"""));

        Assert.Contains("'path'", error.Message, StringComparison.Ordinal);
        Assert.Contains("string", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASizeThatIsNotANumberIsRefused()
    {
        var error = Assert.Throws<JsonException>(() => TemplateSourceObjects.Read(
            $$"""[{"path":"schema.json","size":"1204","sha256":"{{Sha}}"}]"""));

        Assert.Contains("'size'", error.Message, StringComparison.Ordinal);
        Assert.Contains("number", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASizeThatIsNotAnIntegerIsRefused()
    {
        var error = Assert.Throws<JsonException>(() => TemplateSourceObjects.Read(
            $$"""[{"path":"schema.json","size":1.5,"sha256":"{{Sha}}"}]"""));

        Assert.Contains("'size'", error.Message, StringComparison.Ordinal);
        Assert.Contains("integer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANegativeSizeIsRefused()
    {
        var error = Assert.Throws<JsonException>(() => TemplateSourceObjects.Read(
            $$"""[{"path":"schema.json","size":-1,"sha256":"{{Sha}}"}]"""));

        Assert.Contains("'size'", error.Message, StringComparison.Ordinal);
        Assert.Contains("negative", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../schema.json")]
    [InlineData("/schema.json")]
    [InlineData("html\\row.html.sbn")]
    [InlineData("a/b/c/d/e/f/g/h/i.txt")]
    public void APathOutsideThePackageGrammarIsRefused(string path)
    {
        var json = $$"""[{"path":"{{path}}","size":1,"sha256":"{{Sha}}"}]""";

        var error = Assert.Throws<JsonException>(() => TemplateSourceObjects.Read(json));

        Assert.Contains("'path'", error.Message, StringComparison.Ordinal);
        Assert.Contains("package path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedPathIsRefused()
    {
        var error = Assert.Throws<JsonException>(() => TemplateSourceObjects.Read(
            $$"""
            [{"path":"schema.json","size":1,"sha256":"{{Sha}}"},
             {"path":"schema.json","size":2,"sha256":"{{Sha}}"}]
            """));

        Assert.Contains("'path'", error.Message, StringComparison.Ordinal);
        Assert.Contains("twice", error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> HashesThatAreNotSixtyFourLowercaseHexadecimalCharacters => new()
    {
        Sha.ToUpperInvariant(),
        Sha[..63],
        Sha[..63] + "z",
    };

    [Theory]
    [MemberData(nameof(HashesThatAreNotSixtyFourLowercaseHexadecimalCharacters))]
    public void AHashThatIsNotSixtyFourLowercaseHexadecimalCharactersIsRefused(string hash)
    {
        var json = $$"""[{"path":"schema.json","size":1,"sha256":"{{hash}}"}]""";

        var error = Assert.Throws<JsonException>(() => TemplateSourceObjects.Read(json));

        Assert.Contains("'sha256'", error.Message, StringComparison.Ordinal);
        Assert.Contains("64 lower-case hexadecimal", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentThatIsNotAListIsRefused()
    {
        var error = Assert.Throws<JsonException>(() => TemplateSourceObjects.Read(
            """{"path":"schema.json","size":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}"""));

        Assert.Contains("JSON array", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreThanTwoHundredAndFiftySixFilesAreRefused()
    {
        var entries = Enumerable.Range(0, 257)
            .Select(index => $$"""{"path":"file-{{index}}.txt","size":1,"sha256":"{{Sha}}"}""");
        var json = "[" + string.Join(',', entries) + "]";

        var error = Assert.Throws<JsonException>(() => TemplateSourceObjects.Read(json));

        Assert.Contains("256", error.Message, StringComparison.Ordinal);
    }
}
