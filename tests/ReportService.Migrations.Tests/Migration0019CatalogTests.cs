using ReportService.Testing;

namespace ReportService.Migrations.Tests;

/// <summary>
/// The nineteenth migration script (US-008.BOLT-002, SPEC-261125-1224 §8.1, Phase D.5; ADR-048):
/// a new, additive, forward-only script <c>0019_job-partially-succeeded-status.sql</c> that
/// widens the <c>report_jobs.status</c> CHECK and the terminal-tie CHECK to admit the SPEC-fixed
/// stored form <c>PARTIALLY_SUCCEEDED</c>. It is embedded, named per convention and ordered
/// after <c>0018</c>. The real CHECK behaviour (a <c>PARTIALLY_SUCCEEDED</c> row with
/// <c>finished_at</c> set is admitted; the shipped <c>0005</c> is never edited) needs a real
/// PostgreSQL and is asserted on the <c>e2e</c> level, never here (no DB, embedded-catalog only).
/// The oracles come from the approved SPEC, never from the implementation (G06).
///
/// NOTE FOR THE EXECUTOR (SPEC §8.1): adding 0019 to the catalog necessarily breaks the shipped
/// catalog's hard-coded expectations in <c>MigrationCatalogTests</c> (the "18"/ExpectedVersion =
/// 0018 assertions and the ordered-set equality). Those existing assertions must be updated to
/// include 0019 so the catalog/ordering assertions stay green — a required test-code change
/// entailed by Phase D.5, outside this new file's scope-lock. This file states the 0019 oracles.
/// </summary>
public sealed class Migration0019CatalogTests
{
    private const string PartiallySucceeded = "0019_job-partially-succeeded-status.sql";

    private const string AttemptCauses = "0018_pdf-attempt-causes.sql";

    private const string CancelJobIntent = "0020_intent-kind-cancel-job.sql";

    [Fact]
    public void TheNineteenthScriptIsEmbeddedNamedPerConventionAndOrderedAfterTheEighteenth()
    {
        var names = MigrationCatalog.Scripts.Select(script => script.Name).ToList();

        Assert.Contains(PartiallySucceeded, names);
        Assert.True(MigrationScriptName.IsValid(PartiallySucceeded));

        // Ordered strictly after 0018 and immediately before 0020 (US-010.BOLT-001 added the
        // twentieth script, which is now the catalogue's last — see Migration0020CatalogTests).
        Assert.Equal(names.IndexOf(AttemptCauses) + 1, names.IndexOf(PartiallySucceeded));
        Assert.Equal(names.IndexOf(PartiallySucceeded) + 1, names.IndexOf(CancelJobIntent));
    }

    [Fact]
    public void TheScriptWidensTheJobStatusSetToAdmitPartiallySucceededAndIsAdditive()
    {
        var script = Assert.Single(MigrationCatalog.Scripts, candidate => candidate.Name == PartiallySucceeded);

        // The SPEC-fixed stored form the 0019 CHECK admits (D.2/D.5).
        Assert.Contains("PARTIALLY_SUCCEEDED", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE report_service.report_jobs", script.Sql, StringComparison.Ordinal);
        Assert.Contains("status", script.Sql, StringComparison.Ordinal);

        // Additive and forward-only: it only widens allowed sets. It adds no table, drops no
        // table and drops no column — it re-adds CHECK constraints, so DROP CONSTRAINT is allowed.
        Assert.DoesNotContain("CREATE TABLE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP COLUMN", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", script.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheScriptMatchesItsFileInTheSourceTree()
    {
        var script = Assert.Single(MigrationCatalog.Scripts, candidate => candidate.Name == PartiallySucceeded);
        var path = System.IO.Path.Combine(
            RepositoryPaths.Root, "src", "ReportService.Migrations", "Migrations", PartiallySucceeded);
        var bytes = System.IO.File.ReadAllBytes(path);

        Assert.Equal(MigrationScript.ChecksumOf(bytes), script.Checksum);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(bytes), script.Sql);
    }
}
