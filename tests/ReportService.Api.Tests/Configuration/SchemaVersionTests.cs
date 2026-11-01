using ReportService.ServiceDefaults;

namespace ReportService.Api.Tests.Configuration;

/// <summary>
/// The comparison of an expected schema version with the versions a database
/// reports: current, behind, ahead, and unknown when the code states which
/// scripts it embeds.
/// </summary>
public sealed class SchemaVersionTests
{
    private const string Baseline = "0001_baseline.sql";
    private const string Second = "0002_test-table.sql";
    private const string Old = "0000_old.sql";

    [Fact]
    public void NoAppliedRowIsBehindTheExpectedVersion()
    {
        var assessment = SchemaVersion.Assess(Baseline, []);

        Assert.Equal(SchemaStatus.Behind, assessment.Status);
        Assert.Equal(Baseline, assessment.Expected);
        Assert.Equal(SchemaVersion.None, assessment.Applied);
        Assert.Null(assessment.Offender);
    }

    [Fact]
    public void TheExpectedVersionIsCurrent()
    {
        var assessment = SchemaVersion.Assess(Baseline, [Baseline]);

        Assert.Equal(SchemaStatus.Current, assessment.Status);
        Assert.Equal(Baseline, assessment.Applied);
    }

    [Fact]
    public void ARowBelowTheExpectedVersionIsBehind()
    {
        var assessment = SchemaVersion.Assess(Second, [Baseline]);

        Assert.Equal(SchemaStatus.Behind, assessment.Status);
        Assert.Equal(Baseline, assessment.Applied);
    }

    [Fact]
    public void ARowAboveTheExpectedVersionIsAhead()
    {
        var assessment = SchemaVersion.Assess(Baseline, [Baseline, Second]);

        Assert.Equal(SchemaStatus.Ahead, assessment.Status);
        Assert.Equal(Second, assessment.Applied);
    }

    [Fact]
    public void ANameTheCodeDoesNotEmbedIsUnknownAndNamed()
    {
        var assessment = SchemaVersion.Assess(Second, [Baseline, Second, "0003_gone.sql"], [Baseline, Second]);

        Assert.Equal(SchemaStatus.Unknown, assessment.Status);
        Assert.Equal("0003_gone.sql", assessment.Offender);
        Assert.Equal("0003_gone.sql", assessment.Applied);
    }

    [Fact]
    public void AnUnknownNameBelowTheExpectedVersionIsNotDetectedWithoutTheKnownSet()
    {
        // The documented limit: the health check sees one expected version, so a
        // journal that carries a name below it still reports current. The
        // migration runner, which embeds every script, refuses it instead.
        var assessment = SchemaVersion.Assess(Baseline, [Old, Baseline]);

        Assert.Equal(SchemaStatus.Current, assessment.Status);
        Assert.Equal(Baseline, assessment.Applied);
    }

    [Fact]
    public void AGapBelowTheExpectedVersionIsBehindWhenTheKnownSetIsGiven()
    {
        var assessment = SchemaVersion.Assess(Second, [Second], [Baseline, Second]);

        Assert.Equal(SchemaStatus.Behind, assessment.Status);
    }

    [Fact]
    public void AnEmptyScriptSetIsCurrentAtNone()
    {
        var assessment = SchemaVersion.Assess(SchemaVersion.None, [], []);

        Assert.Equal(SchemaStatus.Current, assessment.Status);
        Assert.Equal(SchemaVersion.None, assessment.Applied);
    }

    [Fact]
    public void AnUnknownNameIsUnknownBeforeTheVersionIsCompared()
    {
        var assessment = SchemaVersion.Assess(SchemaVersion.None, ["0001_other.sql"], []);

        Assert.Equal(SchemaStatus.Unknown, assessment.Status);
    }

    [Fact]
    public void NamesAreComparedOrdinallyAndBlankRowsAreIgnored()
    {
        var assessment = SchemaVersion.Assess(Second, [null, "  ", Second, Baseline]);

        Assert.Equal(SchemaStatus.Current, assessment.Status);
        Assert.Equal(Second, assessment.Applied);
    }

    [Theory]
    [InlineData("0001_baseline.sql", true)]
    [InlineData("0002_test-table.sql", true)]
    [InlineData("0001_a.sql", true)]
    [InlineData("001_baseline.sql", false)]
    [InlineData("00001_baseline.sql", false)]
    [InlineData("0001_Baseline.sql", false)]
    [InlineData("0001_.sql", false)]
    [InlineData("0001_baseline.SQL", false)]
    [InlineData("0001_baseline.sql.bak", false)]
    [InlineData("baseline.sql", false)]
    [InlineData("", false)]
    public void TheScriptNameRuleTakesFourDigitsAndALowerCaseSlug(string name, bool valid) =>
        Assert.Equal(valid, SchemaVersion.IsScriptName(name));

    [Fact]
    public void ANullNameIsNotAScriptName() => Assert.False(SchemaVersion.IsScriptName(null));
}
