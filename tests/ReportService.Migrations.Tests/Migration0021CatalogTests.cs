using ReportService.Testing;

namespace ReportService.Migrations.Tests;

/// <summary>
/// The twenty-first script: the declared priority and the effective class of a job, two
/// columns defaulting to <c>normal</c> and checked against the three levels, and the guard
/// that refuses any change to the declared priority once the row exists. These cases read the
/// script as shipped; that the guard really refuses an update is proved against the database
/// by the end-to-end level, which runs this script on PostgreSQL.
/// </summary>
public sealed class Migration0021CatalogTests
{
    private const string JobPriority = "0021_report-job-priority.sql";

    private const string CancelJobIntent = "0020_intent-kind-cancel-job.sql";

    [Fact]
    public void TheTwentyFirstScriptIsEmbeddedLastAndIsTheExpectedVersion()
    {
        var names = MigrationCatalog.Scripts.Select(script => script.Name).ToList();

        Assert.True(MigrationScriptName.IsValid(JobPriority));
        Assert.Equal(JobPriority, MigrationCatalog.Scripts[^1].Name);
        Assert.Equal(JobPriority, MigrationCatalog.ExpectedVersion);
        Assert.Equal(names.IndexOf(CancelJobIntent) + 1, names.IndexOf(JobPriority));
    }

    [Fact]
    public void TheScriptAddsTheTwoPriorityColumnsWithTheirDefaultAndTheirCheck()
    {
        var sql = Script().Sql;

        Assert.Contains("ALTER TABLE report_service.report_jobs", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN declared_priority text NOT NULL DEFAULT 'normal'", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN effective_priority_class text NOT NULL DEFAULT 'normal'", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (declared_priority IN ('low', 'normal', 'high'))", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (effective_priority_class IN ('low', 'normal', 'high'))", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScriptGuardsTheDeclaredPriorityAgainstAnyUpdateAndLeavesTheEffectiveClassUpdatable()
    {
        var sql = Script().Sql;

        Assert.Contains("CREATE FUNCTION report_service.report_jobs_declared_priority_guard()", sql, StringComparison.Ordinal);
        Assert.Contains("IF NEW.declared_priority IS DISTINCT FROM OLD.declared_priority THEN", sql, StringComparison.Ordinal);
        Assert.Contains("ERRCODE = 'check_violation'", sql, StringComparison.Ordinal);
        Assert.Contains("BEFORE UPDATE ON report_service.report_jobs", sql, StringComparison.Ordinal);
        Assert.Contains("FOR EACH ROW EXECUTE FUNCTION report_service.report_jobs_declared_priority_guard()", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("NEW.effective_priority_class", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScriptIsAdditive()
    {
        var sql = Script().Sql;

        Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheScriptMatchesItsFileInTheSourceTree()
    {
        var script = Script();
        var path = System.IO.Path.Combine(
            RepositoryPaths.Root, "src", "ReportService.Migrations", "Migrations", JobPriority);
        var bytes = System.IO.File.ReadAllBytes(path);

        Assert.Equal(MigrationScript.ChecksumOf(bytes), script.Checksum);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(bytes), script.Sql);
    }

    private static MigrationScript Script() =>
        Assert.Single(MigrationCatalog.Scripts, candidate => candidate.Name == JobPriority);
}
