using ReportService.Testing;

namespace ReportService.Migrations.Tests;

/// <summary>
/// The twentieth migration script (US-010.BOLT-001, SPEC-261126-0252 Phase A.1): a new,
/// additive, forward-only script <c>0020_intent-kind-cancel-job.sql</c> that widens the
/// <c>intents_kind_check</c> constraint to admit the stored intent kind <c>cancel-job</c> the
/// cancel request surface records. It is embedded, named per convention, ordered after
/// <c>0019</c> and is now the catalogue's last, so it is the expected schema version. The real
/// CHECK behaviour (a <c>cancel-job</c> intent row is admitted; the shipped <c>0003</c>/<c>0005</c>
/// are never edited) needs a real PostgreSQL and is asserted on the <c>e2e</c> level, never here
/// (no DB, embedded-catalog only). The oracles come from the approved SPEC, never from the
/// implementation (G06). Updating the shipped-catalogue count and last-script expectations in
/// <c>MigrationCatalogTests</c> and <c>Migration0019CatalogTests</c> is the required test-code
/// change this new script entails (SPEC §6; the same note the 0019 catalogue test records).
/// </summary>
public sealed class Migration0020CatalogTests
{
    private const string CancelJobIntent = "0020_intent-kind-cancel-job.sql";

    private const string PartiallySucceeded = "0019_job-partially-succeeded-status.sql";

    [Fact]
    public void TheTwentiethScriptIsEmbeddedNamedPerConventionAndOrderedAfterTheNineteenth()
    {
        var names = MigrationCatalog.Scripts.Select(script => script.Name).ToList();

        Assert.Contains(CancelJobIntent, names);
        Assert.True(MigrationScriptName.IsValid(CancelJobIntent));

        // Ordered strictly after 0019, with no other script between them.
        Assert.Equal(names.IndexOf(PartiallySucceeded) + 1, names.IndexOf(CancelJobIntent));
    }

    [Fact]
    public void TheScriptWidensTheIntentKindSetToAdmitCancelJobAndIsAdditive()
    {
        var script = Assert.Single(MigrationCatalog.Scripts, candidate => candidate.Name == CancelJobIntent);

        // The SPEC-fixed stored kind the 0020 CHECK admits, beside the two it already did.
        Assert.Contains("cancel-job", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE report_service.intents", script.Sql, StringComparison.Ordinal);
        Assert.Contains("intents_kind_check", script.Sql, StringComparison.Ordinal);
        Assert.Contains("start-upload-verification", script.Sql, StringComparison.Ordinal);
        Assert.Contains("start-job", script.Sql, StringComparison.Ordinal);

        // Additive and forward-only: it only re-adds the CHECK constraint over a wider allowed
        // set. It adds no table, drops no table and drops no column — it re-adds a CHECK
        // constraint, so DROP CONSTRAINT is allowed.
        Assert.DoesNotContain("CREATE TABLE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP COLUMN", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", script.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheScriptMatchesItsFileInTheSourceTree()
    {
        var script = Assert.Single(MigrationCatalog.Scripts, candidate => candidate.Name == CancelJobIntent);
        var path = System.IO.Path.Combine(
            RepositoryPaths.Root, "src", "ReportService.Migrations", "Migrations", CancelJobIntent);
        var bytes = System.IO.File.ReadAllBytes(path);

        Assert.Equal(MigrationScript.ChecksumOf(bytes), script.Checksum);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(bytes), script.Sql);
    }
}
