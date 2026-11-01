namespace ReportService.Migrations.Tests;

/// <summary>
/// The lines one run writes: one per applied script, the current version, the
/// refusals and the failure — and never a connection string.
/// </summary>
public sealed class MigrationResultTests
{
    private const string Baseline = "0001_baseline.sql";
    private const string Second = "0002_test-table.sql";
    private const string PlantedConnectionString = "Host=127.0.0.1;Username=reportservice;Password=planted-7b1c";

    [Fact]
    public void AnAppliedRunWritesOneLinePerScriptAndTheCurrentVersion()
    {
        var result = Result([Baseline, Second], []);

        Assert.True(result.Succeeded);
        Assert.Equal(["applied '0001_baseline.sql'", "applied '0002_test-table.sql'", "schema is current at 0002_test-table.sql"], result.Lines());
        Assert.Equal(3, result.OutputLines.Count);
        Assert.Empty(result.ErrorLines);
    }

    [Fact]
    public void ARunWithNothingToDoSaysSoAndDoesNotMentionAnApplication()
    {
        var result = Result([], []);

        Assert.True(result.Succeeded);
        Assert.Equal(["no change; schema is current at 0001_baseline.sql"], result.Lines());
    }

    [Fact]
    public void ARefusedRunWritesTheRefusalAndIsNotSuccessful()
    {
        var result = Result([], ["migration '0001_baseline.sql' changed after it was applied"]);

        Assert.False(result.Succeeded);
        Assert.Equal(["migration '0001_baseline.sql' changed after it was applied"], result.Lines());
        Assert.Equal(["migration '0001_baseline.sql' changed after it was applied"], result.ErrorLines);
        Assert.Empty(result.OutputLines);
    }

    [Fact]
    public void AFailedScriptWritesItsNamePositionSqlstateAndMessage()
    {
        var failure = new MigrationFailure("0002_test-table.sql", 42, "42P01", "PostgresException", "relation \"x\" does not exist");
        var result = new MigrationResult([Baseline], [], Baseline, [], failure);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "migration '0002_test-table.sql' failed at position 42: 42P01 relation \"x\" does not exist",
            Assert.Single(result.ErrorLines));
        Assert.Equal(["applied '0001_baseline.sql'", "migration '0002_test-table.sql' failed at position 42: 42P01 relation \"x\" does not exist"], result.Lines());
    }

    [Fact]
    public void AFailureBeforeAnyScriptNamesNoScriptAndNoStatement()
    {
        var result = new MigrationResult([], [], "none", [], new MigrationFailure(null, 0, null, "NpgsqlException", string.Empty));

        Assert.Equal("migration run failed before any script was applied: NpgsqlException", Assert.Single(result.ErrorLines));
    }

    [Fact]
    public void AFailureWithoutASqlStateStillNamesTheFailure()
    {
        var failure = new MigrationFailure("0002_test-table.sql", 0, null, "NpgsqlException", string.Empty);

        Assert.Equal("migration '0002_test-table.sql' failed at position 0: no SQLSTATE", failure.Line);
    }

    [Fact]
    public void NoLineEverCarriesAConnectionString()
    {
        var failure = new MigrationFailure(Baseline, 1, "08006", "NpgsqlException", "the server closed the connection");
        var result = new MigrationResult([Second], [], Second, ["a refusal"], failure);
        var lines = string.Join("\n", result.Lines()) + string.Join("\n", result.OutputLines) + string.Join("\n", result.ErrorLines);

        Assert.DoesNotContain("Password", lines, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", lines, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PlantedConnectionString, lines, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCurrentVersionIsWhateverTheRunnerStates()
    {
        var result = new MigrationResult([Second], [], "0002_test-table.sql", [], null);

        Assert.Equal("schema is current at 0002_test-table.sql", result.StatusLine);
        Assert.Contains("0002_test-table.sql", result.StatusLine, StringComparison.Ordinal);
    }

    private static MigrationResult Result(IReadOnlyList<string> applied, IReadOnlyList<string> refusals) =>
        new(applied, [], applied.Count == 0 ? Baseline : applied[^1], refusals, null);
}
