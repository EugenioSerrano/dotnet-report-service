using System.Text.Json;

namespace ReportService.Jobs.Tests;

/// <summary>
/// What produced a block's presentation, as the column stores it: the seven members written
/// in one object, the one value that says a build recorded none, and the record errors of a
/// value that is neither - so a reader is never handed a half-filled object.
/// </summary>
public sealed class PresentationProvenanceTests
{
    private static readonly PresentationProvenance Complete = new(
        "es-AR",
        "America/Argentina/Buenos_Aires",
        new DateTimeOffset(2026, 3, 1, 15, 30, 0, TimeSpan.FromHours(-3)),
        "scriban",
        "7.5.1",
        "74.2",
        "2025b");

    [Fact]
    public void TheSevenMembersAreWrittenInOneObjectAndReadBack()
    {
        var json = Complete.ToJson();

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("es-AR", root.GetProperty("culture").GetString());
        Assert.Equal("America/Argentina/Buenos_Aires", root.GetProperty("timeZone").GetString());
        Assert.Equal("2026-03-01T18:30:00.0000000+00:00", root.GetProperty("clock").GetString());
        Assert.Equal("scriban", root.GetProperty("engine").GetString());
        Assert.Equal("7.5.1", root.GetProperty("engineVersion").GetString());
        Assert.Equal("74.2", root.GetProperty("icuVersion").GetString());
        Assert.Equal("2025b", root.GetProperty("tzDataVersion").GetString());

        Assert.Equal(Complete, PresentationProvenance.Parse(json));
    }

    [Fact]
    public void TheValueABuildThatRecordedNoneLeavesReadsAsNone()
    {
        Assert.Null(PresentationProvenance.Parse(PresentationProvenance.UnrecordedJson));
        Assert.Null(PresentationProvenance.Parse(null));
        Assert.Null(PresentationProvenance.Parse("  "));
    }

    [Theory]
    [InlineData("""{"source":"unrecorded","culture":"es-AR"}""")]
    [InlineData("""{"culture":"es-AR","timeZone":"America/Argentina/Buenos_Aires","clock":"2026-03-01T18:30:00.0000000+00:00","engine":"scriban","engineVersion":"7.5.1","icuVersion":"74.2"}""")]
    [InlineData("""{"culture":"es-AR","timeZone":"America/Argentina/Buenos_Aires","clock":18,"engine":"scriban","engineVersion":"7.5.1","icuVersion":"74.2","tzDataVersion":"2025b"}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void AValueThatIsNeitherShapeIsAnErrorOfTheRecord(string json) =>
        Assert.ThrowsAny<Exception>(() => PresentationProvenance.Parse(json));
}
