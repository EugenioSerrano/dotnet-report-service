using System.Text;
using ReportService.ServiceDefaults;
using ReportService.Testing;

namespace ReportService.Migrations.Tests;

/// <summary>
/// The scripts embedded in the assembly: the shipped set in name order, each
/// checksum equal to its file in the source tree, and the validation a bad name,
/// a number used twice or an empty script is refused by.
/// </summary>
public sealed class MigrationCatalogTests
{
    private const string Baseline = "0001_baseline.sql";

    private const string Uploads = "0002_uploads.sql";

    private const string TemplateVersions = "0003_template-versions.sql";

    [Fact]
    public void TheShippedSetIsTheBaselineAndTheUploadsTableInNameOrder()
    {
        Assert.Equal(new[] { Baseline, Uploads, TemplateVersions }, MigrationCatalog.Scripts.Select(script => script.Name));
        Assert.All(MigrationCatalog.Scripts, script => Assert.True(MigrationScriptName.IsValid(script.Name)));
        Assert.Equal(TemplateVersions, MigrationCatalog.ExpectedVersion);
    }

    [Fact]
    public void EveryShippedScriptMatchesItsFileInTheSourceTree()
    {
        foreach (var script in MigrationCatalog.Scripts)
        {
            var path = Path.Combine(RepositoryPaths.Root, "src", "ReportService.Migrations", "Migrations", script.Name);
            var bytes = File.ReadAllBytes(path);

            Assert.Equal(MigrationScript.ChecksumOf(bytes), script.Checksum);
            Assert.Equal(Encoding.UTF8.GetString(bytes), script.Sql);
        }
    }

    [Fact]
    public void TheBaselineCreatesTheSchemaAndTheUploadsScriptAddsTheSessionsTable()
    {
        var baseline = Assert.Single(MigrationCatalog.Scripts, script => script.Name == Baseline);
        var uploads = Assert.Single(MigrationCatalog.Scripts, script => script.Name == Uploads);

        Assert.DoesNotContain("CREATE TABLE", baseline.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS report_service;", baseline.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE report_service.uploads", uploads.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChecksumIsTheLowercaseHexSha256OfAKnownByteString()
    {
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            MigrationScript.ChecksumOf("abc"u8));
    }

    [Fact]
    public void ScriptsAreOrderedByTheirName()
    {
        var scripts = MigrationCatalog.Create(
        [
            (Name: "0010_ten.sql", Bytes: "SELECT 10"u8.ToArray()),
            (Name: "0002_two.sql", Bytes: "SELECT 2"u8.ToArray()),
        ]);

        Assert.Equal(["0002_two.sql", "0010_ten.sql"], scripts.Select(script => script.Name));
        Assert.Equal("0010_ten.sql", MigrationCatalog.ExpectedVersionOf(scripts));
    }

    [Fact]
    public void AMalformedNameIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MigrationCatalog.Create([(Name: "1_baseline.sql", Bytes: "SELECT 1"u8.ToArray())]));

        Assert.Contains("does not follow the name rule", error.Message, StringComparison.Ordinal);
        Assert.Contains("1_baseline.sql", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANumberUsedTwiceIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MigrationCatalog.Create(
            [
                (Name: "0002_two.sql", Bytes: "SELECT 2"u8.ToArray()),
                (Name: "0002_other.sql", Bytes: "SELECT 3"u8.ToArray()),
            ]));

        Assert.Contains("migration script number 0002 is used twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyScriptIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MigrationCatalog.Create([(Name: "0001_baseline.sql", Bytes: []), (Name: "0002_blank.sql", Bytes: "  \n"u8.ToArray())]));

        Assert.Equal("migration script '0001_baseline.sql' is empty", error.Message);
    }

    [Fact]
    public void AnEmptySetIsCurrentAtNone()
    {
        var scripts = MigrationCatalog.Create([]);

        Assert.Empty(scripts);
        Assert.Equal(SchemaVersion.None, MigrationCatalog.ExpectedVersionOf(scripts));
    }
}
