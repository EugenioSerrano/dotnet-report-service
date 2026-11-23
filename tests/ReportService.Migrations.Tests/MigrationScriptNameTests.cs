namespace ReportService.Migrations.Tests;

/// <summary>The rule a script's file name follows: a fixed width, so the ordinal order is the numeric order.</summary>
public sealed class MigrationScriptNameTests
{
    [Theory]
    [InlineData("0001_baseline.sql", true)]
    [InlineData("0042_add-job-table.sql", true)]
    [InlineData("0042_add-job-table-v2.sql", true)]
    [InlineData("001_baseline.sql", false)]
    [InlineData("00001_baseline.sql", false)]
    [InlineData("0001_Baseline.sql", false)]
    [InlineData("0001_baseline_slug.sql", false)]
    [InlineData("0001_.sql", false)]
    [InlineData("0001_baseline.sql.txt", false)]
    [InlineData("0001baseline.sql", false)]
    [InlineData("", false)]
    public void TheNameRuleTakesFourDigitsAndALowerCaseSlug(string name, bool valid) =>
        Assert.Equal(valid, MigrationScriptName.IsValid(name));

    [Fact]
    public void ANullNameIsNotValid() => Assert.False(MigrationScriptName.IsValid(null));

    [Fact]
    public void AMalformedNameIsRefusedWithAMessageNamingIt()
    {
        var problem = MigrationScriptName.Problem("1_baseline.sql");

        Assert.Equal("migration script '1_baseline.sql' does not follow the name rule 0000_<slug>.sql", problem);
    }

    [Fact]
    public void AValidNameHasNoProblem() => Assert.Null(MigrationScriptName.Problem("0001_baseline.sql"));

    [Fact]
    public void TheNumberIsTheFourDigitPrefixWhichOrdersTheScripts()
    {
        Assert.Equal("0001", MigrationScriptName.Number("0001_baseline.sql"));
        Assert.True(string.CompareOrdinal("0009_z.sql", "0010_a.sql") < 0);
        Assert.True(string.CompareOrdinal("0100_z.sql", "0999_a.sql") < 0);
    }
}
