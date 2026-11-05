using ReportService.Testing.Manifests;

namespace ReportService.TemplatePackages.Tests.Manifest;

/// <summary>
/// Whether two manifest texts are the same document: the members and their
/// values are what is compared, the member order and the spacing are not, and a
/// member or a value that is lost, added or changed is not the same document.
/// </summary>
public sealed class ManifestDocumentsTests
{
    private const string Compact =
        """{"manifestVersion":"1.0","id":"round-trip","version":"1.1.0","owner":"billing-system","shared":false,"engine":"scriban","engineVersion":"7.5.1","dataContract":{"profile":"header-items-v1","version":"1.0","schema":"schema/data.schema.json"}}""";

    private const string SpacedAndReordered =
        """{"id": "round-trip", "manifestVersion": "1.0", "version": "1.1.0", "owner": "billing-system", "shared": false, "engine": "scriban", "engineVersion": "7.5.1", "dataContract": {"profile": "header-items-v1", "version": "1.0", "schema": "schema/data.schema.json"}}""";

    [Fact]
    public void TheSameDocumentWrittenInAnotherMemberOrderAndSpacingIsTheSame()
    {
        Assert.True(ManifestDocuments.AreTheSameDocument(Compact, SpacedAndReordered));
    }

    [Fact]
    public void TheTextOfTheRecordedFailureIsTheSameDocumentAsWhatWasWritten()
    {
        // The two shapes the failing case showed: the text a take records, compact
        // and in document order, against the text the column returns, spaced and
        // with its own member order. The same document is the same document, and the
        // two texts are not the same text - which is why a byte comparison failed.
        const string Written =
            """{"manifestVersion":"1.0","id":"round-trip","version":"1.1.0","owner":"billing-system","shared":false,"engine":"scriban","engineVersion":"7.5.1"}""";
        const string Read =
            """{"id": "round-trip", "owner": "billing-system", "engine": "scriban", "shared": false, "version": "1.1.0", "engineVersion": "7.5.1", "manifestVersion": "1.0"}""";

        Assert.True(ManifestDocuments.AreTheSameDocument(Written, Read));
        Assert.NotEqual(Written, Read);
    }

    [Fact]
    public void AMemberTheTakeWroteThatTheReadLacksIsNotTheSame()
    {
        const string WithoutOwner =
            """{"manifestVersion":"1.0","id":"round-trip","version":"1.1.0","shared":false,"engine":"scriban","engineVersion":"7.5.1"}""";

        Assert.False(ManifestDocuments.AreTheSameDocument(Compact, WithoutOwner));
    }

    [Fact]
    public void AValueChangedIsNotTheSame()
    {
        const string AnotherOwner =
            """{"manifestVersion":"1.0","id":"round-trip","version":"1.1.0","owner":"payments-system","shared":false,"engine":"scriban","engineVersion":"7.5.1","dataContract":{"profile":"header-items-v1","version":"1.0","schema":"schema/data.schema.json"}}""";

        Assert.False(ManifestDocuments.AreTheSameDocument(Compact, AnotherOwner));
    }

    [Fact]
    public void AMemberTheTakeDidNotWriteIsNotTheSame()
    {
        const string WithOneMoreMember =
            """{"manifestVersion":"1.0","id":"round-trip","version":"1.1.0","owner":"billing-system","shared":false,"engine":"scriban","engineVersion":"7.5.1","dataContract":{"profile":"header-items-v1","version":"1.0","schema":"schema/data.schema.json"},"signedBy":"nobody"}""";

        Assert.False(ManifestDocuments.AreTheSameDocument(Compact, WithOneMoreMember));
    }

    [Fact]
    public void AnArrayInAnotherOrderIsNotTheSame()
    {
        const string Written = """{"parts":["document-start","row","document-end"]}""";
        const string Reordered = """{"parts":["row","document-start","document-end"]}""";

        Assert.False(ManifestDocuments.AreTheSameDocument(Written, Reordered));
    }

    [Fact]
    public void TheSameNumberWrittenAnotherWayIsTheSame()
    {
        Assert.True(ManifestDocuments.AreTheSameDocument("""{"scale":1e2}""", """{"scale":100}"""));
        Assert.True(ManifestDocuments.AreTheSameDocument("""{"top":15.50}""", """{"top":15.5}"""));
    }

    [Fact]
    public void ANumberOfAnotherValueIsNotTheSame()
    {
        Assert.False(ManifestDocuments.AreTheSameDocument("""{"top":15.50}""", """{"top":15.51}"""));
    }

    [Fact]
    public void AStringAndTheNumberItLooksLikeAreNotTheSame()
    {
        Assert.False(ManifestDocuments.AreTheSameDocument("""{"schema":"1.0"}""", """{"schema":1.0}"""));
    }

    [Fact]
    public void AMemberNameInAnotherCaseIsAnotherMember()
    {
        Assert.False(ManifestDocuments.AreTheSameDocument("""{"id":"round-trip"}""", """{"ID":"round-trip"}"""));
    }
}
